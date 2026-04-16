using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class WindowsSpeechService : ISpeechService, IDisposable
{
    private readonly PiperSpeechService _piperSpeechService;
    private readonly WindowsNeuralSpeechService _preferredSpeechService;
    private readonly SystemSpeechService _fallbackSpeechService;
    private readonly SpeechVoice[] _installedVoices;
    private bool _disposed;

    public WindowsSpeechService()
    {
        _piperSpeechService = new PiperSpeechService();
        _preferredSpeechService = new WindowsNeuralSpeechService();
        _fallbackSpeechService = new SystemSpeechService();
        _installedVoices = _piperSpeechService
            .GetInstalledVoices()
            .Concat(_preferredSpeechService.GetInstalledVoices())
            .Concat(_fallbackSpeechService.GetInstalledVoices())
            .GroupBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(voice => GetEnginePriority(voice.Engine))
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsSpeaking =>
        _piperSpeechService.IsSpeaking ||
        _preferredSpeechService.IsSpeaking ||
        _fallbackSpeechService.IsSpeaking;

    public IReadOnlyList<SpeechVoice> GetInstalledVoices() => _installedVoices;

    public async Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var explicitVoiceName = request.Options.VoiceName;
        var engineOrder = BuildEngineOrder(explicitVoiceName);
        if (!string.IsNullOrWhiteSpace(explicitVoiceName) && engineOrder.Count == 0)
        {
            return SpeechResult.Failed($"Selected voice '{explicitVoiceName}' is not installed.");
        }

        SpeechResult? firstFailure = null;
        foreach (var engine in engineOrder)
        {
            var result = await engine.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Success || result.WasCancelled)
            {
                return result;
            }

            firstFailure ??= result;
            AppDiagnostics.Warn(
                "speech_fallback_engaged",
                new Dictionary<string, string?>
                {
                    ["engine"] = GetEngineName(engine),
                    ["voice"] = request.Options.VoiceName,
                    ["reason"] = result.Message
                });
        }

        return firstFailure ?? SpeechResult.Failed("Couldn't start reading.");
    }

    public async Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_piperSpeechService.IsSpeaking)
        {
            return await _piperSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_preferredSpeechService.IsSpeaking)
        {
            return await _preferredSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_fallbackSpeechService.IsSpeaking)
        {
            return await _fallbackSpeechService.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        return SpeechResult.Completed("Speech is already stopped.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _piperSpeechService.Dispose();
        _preferredSpeechService.Dispose();
        _fallbackSpeechService.Dispose();
        _disposed = true;
    }

    private IReadOnlyList<ISpeechService> BuildEngineOrder(string? voiceName)
    {
        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            if (_piperSpeechService.SupportsVoice(voiceName))
            {
                return new[] { (ISpeechService)_piperSpeechService };
            }

            if (_preferredSpeechService.SupportsVoice(voiceName))
            {
                return new[] { (ISpeechService)_preferredSpeechService };
            }

            if (_fallbackSpeechService.SupportsVoice(voiceName))
            {
                return new[] { (ISpeechService)_fallbackSpeechService };
            }

            return Array.Empty<ISpeechService>();
        }

        var engines = new List<ISpeechService>(3);
        AddEngine(engines, _preferredSpeechService);
        AddEngine(engines, _fallbackSpeechService);
        if (engines.Count == 0 && _piperSpeechService.HasUsableInstallation)
        {
            // Last-resort fallback only when Windows engines are unavailable.
            AddEngine(engines, _piperSpeechService);
        }

        return engines;
    }

    private static void AddEngine(ICollection<ISpeechService> engines, ISpeechService engine)
    {
        if (!engines.Contains(engine))
        {
            engines.Add(engine);
        }
    }

    private static string GetEngineName(ISpeechService engine)
    {
        return engine switch
        {
            PiperSpeechService => "piper",
            WindowsNeuralSpeechService => "windows_onecore",
            SystemSpeechService => "system_speech",
            _ => engine.GetType().Name
        };
    }

    private static int GetEnginePriority(string engine)
    {
        return engine switch
        {
            "Piper" => 0,
            "Windows OneCore" => 1,
            "System.Speech" => 2,
            _ => 9
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsSpeechService));
        }
    }
}

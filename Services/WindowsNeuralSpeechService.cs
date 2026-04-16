using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;
using Windows.Media.SpeechSynthesis;

namespace RightSpeak.Services;

internal sealed class WindowsNeuralSpeechService : ISpeechService, IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly VoiceInformation[] _installedVoices;
    private readonly string[] _installedVoiceNames;
    private readonly string? _defaultVoiceName;
    private CancellationTokenSource? _playbackCancellationTokenSource;
    private SoundPlayer? _currentSoundPlayer;
    private bool _disposed;

    public WindowsNeuralSpeechService()
    {
        using var voiceProbe = new SpeechSynthesizer();
        _gate = new SemaphoreSlim(1, 1);
        _installedVoices = SpeechSynthesizer.AllVoices
            .OrderBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _installedVoiceNames = _installedVoices
            .Select(GetVoiceName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _defaultVoiceName = voiceProbe.Voice is null ? null : GetVoiceName(voiceProbe.Voice);
    }

    public bool IsSpeaking { get; private set; }

    public IReadOnlyList<SpeechVoice> GetInstalledVoices()
    {
        return _installedVoices
            .Select(voice => new SpeechVoice(GetVoiceName(voice), GetVoiceName(voice), "Windows OneCore"))
            .ToArray();
    }

    public async Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        var text = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return SpeechResult.Failed("Nothing to read. Enter text first.");
        }

        CancellationTokenSource playbackCancellationTokenSource;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelPlaybackUnsafe();

            playbackCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _playbackCancellationTokenSource = playbackCancellationTokenSource;
            IsSpeaking = true;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await PlayRenderedAudioAsync(text, request.Options, playbackCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "windows_neural_speech_playback_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return SpeechResult.Failed("Couldn't start reading.");
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _currentSoundPlayer?.Stop();
                _currentSoundPlayer?.Dispose();
                _currentSoundPlayer = null;

                _playbackCancellationTokenSource?.Dispose();
                _playbackCancellationTokenSource = null;
                IsSpeaking = false;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (!IsSpeaking)
            {
                return SpeechResult.Completed("Speech is already stopped.");
            }

            CancelPlaybackUnsafe();
            IsSpeaking = false;
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "windows_neural_speech_stop_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return SpeechResult.Failed("Couldn't stop reading.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _playbackCancellationTokenSource?.Cancel();
        _currentSoundPlayer?.Stop();
        _currentSoundPlayer?.Dispose();
        _playbackCancellationTokenSource?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }

    public bool SupportsVoice(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return false;
        }

        return _installedVoiceNames.Any(name => string.Equals(name, voiceName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<SpeechResult> PlayRenderedAudioAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        var waveBytes = await RenderWaveBytesAsync(text, options, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var playbackDuration = SpeechAudioHelper.GetPlaybackDuration(waveBytes);
        using var waveStream = new MemoryStream(waveBytes, writable: false);
        using var soundPlayer = new SoundPlayer(waveStream);
        soundPlayer.Load();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _currentSoundPlayer = soundPlayer;
        }
        finally
        {
            _gate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        soundPlayer.Play();
        await Task.Delay(playbackDuration, cancellationToken).ConfigureAwait(false);

        return SpeechResult.Completed();
    }

    private async Task<byte[]> RenderWaveBytesAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        using var synthesizer = new SpeechSynthesizer();
        ApplyOptions(synthesizer, options);

        using var speechStream = await synthesizer.SynthesizeTextToStreamAsync(text);
        cancellationToken.ThrowIfCancellationRequested();

        using var managedStream = speechStream.AsStreamForRead();
        using var waveStream = new MemoryStream();
        await managedStream.CopyToAsync(waveStream, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var primerSeconds = ResolvePrimerSeconds(options);
        return SpeechAudioHelper.PrependPrimerWave(waveStream.ToArray(), primerSeconds);
    }

    private static double ResolvePrimerSeconds(SpeechOptions options)
    {
        if (options.LeadingPrimerSecondsOverride is not double overrideValue)
        {
            return 0.85;
        }

        return Math.Clamp(overrideValue, 0, 2.0);
    }

    private void ApplyOptions(SpeechSynthesizer synthesizer, SpeechOptions options)
    {
        var voiceName = options.VoiceName;
        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            var selectedVoice = FindVoice(voiceName);
            if (selectedVoice is not null)
            {
                synthesizer.Voice = selectedVoice;
            }
        }
        else if (!string.IsNullOrWhiteSpace(_defaultVoiceName))
        {
            var defaultVoice = FindVoice(_defaultVoiceName);
            if (defaultVoice is not null)
            {
                synthesizer.Voice = defaultVoice;
            }
        }

        if (options.Rate != 0)
        {
            synthesizer.Options.SpeakingRate = MapSpeechRate(options.Rate);
        }
    }

    private VoiceInformation? FindVoice(string voiceName)
    {
        return _installedVoices.FirstOrDefault(voice =>
            string.Equals(GetVoiceName(voice), voiceName, StringComparison.OrdinalIgnoreCase));
    }

    private void CancelPlaybackUnsafe()
    {
        _playbackCancellationTokenSource?.Cancel();
        _currentSoundPlayer?.Stop();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsNeuralSpeechService));
        }
    }

    private static string GetVoiceName(VoiceInformation voice)
    {
        return string.IsNullOrWhiteSpace(voice.DisplayName) ? voice.Id : voice.DisplayName;
    }

    private static double MapSpeechRate(int rate)
    {
        return Math.Clamp(rate / 10d, -1d, 2d);
    }
}

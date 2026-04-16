using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

internal sealed class PiperSpeechService : ISpeechService, IDisposable
{
    private const string PiperVoicePrefix = "piper:";
    private const int ReusedPiperOutputPollMilliseconds = 25;
    private const int ReusedPiperOutputTimeoutMilliseconds = 30000;
    private const int ReusedPiperReadRetryMilliseconds = 25;
    private const int ReusedPiperReadRetries = 60;
    private const int WarmSessionIdleResetSeconds = 6;
    private const double PiperPrimerColdStartSeconds = 1.00;
    private const double PiperPrimerWarmStartSeconds = 0.55;
    private const double PiperPrimerLongTextSeconds = 0.30;
    private const double PiperPrimerMinSeconds = 0.05;
    private const double PiperPrimerMaxSeconds = 1.25;
    private static readonly string[] PreferredDefaultVoices =
    {
        "piper:en_US-ljspeech-high",
        "piper:en_GB-cori-high"
    };

    private readonly SemaphoreSlim _gate;
    private readonly string? _piperExecutablePath;
    private readonly PiperVoiceDefinition[] _voices;
    private readonly PiperAvailability _availability;
    private CancellationTokenSource? _playbackCancellationTokenSource;
    private SoundPlayer? _currentSoundPlayer;
    private Process? _currentPiperProcess;
    private Process? _warmPiperProcess;
    private StreamWriter? _warmPiperInputWriter;
    private string? _warmPiperOutputDirectory;
    private string? _warmPiperModelPath;
    private string? _warmPiperConfigPath;
    private double _warmPiperLengthScale;
    private DateTime _lastPlaybackCompletedUtc;
    private bool _disposed;

    public PiperSpeechService()
    {
        _gate = new SemaphoreSlim(1, 1);
        _piperExecutablePath = LocatePiperExecutable();
        _voices = DiscoverVoices().ToArray();
        _availability = ProbeAvailability(_piperExecutablePath, _voices);
        _lastPlaybackCompletedUtc = DateTime.MinValue;
    }

    public bool IsSpeaking { get; private set; }

    public bool HasUsableInstallation => _availability.IsAvailable;
    public string? AvailabilityFailureReason => _availability.FailureReason;

    public IReadOnlyList<SpeechVoice> GetInstalledVoices()
    {
        if (!_availability.IsAvailable)
        {
            return Array.Empty<SpeechVoice>();
        }

        return _voices
            .Select(voice => new SpeechVoice(voice.Name, voice.DisplayName, "Piper"))
            .ToArray();
    }

    public bool SupportsVoice(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return false;
        }

        return _voices.Any(voice => string.Equals(voice.Name, voiceName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!_availability.IsAvailable)
        {
            return SpeechResult.Failed(_availability.FailureReason ?? "Piper runtime isn't available.");
        }

        var text = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return SpeechResult.Failed("Nothing to read. Enter text first.");
        }

        var voice = ResolveVoice(request.Options.VoiceName);
        if (voice is null)
        {
            return SpeechResult.Failed("No Piper voice is installed.");
        }

        CancellationTokenSource playbackCancellationTokenSource;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelActivePlaybackUnsafe();

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
            return await PlayRenderedAudioAsync(text, request.Options, voice, playbackCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "piper_speech_playback_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message,
                    ["voice"] = voice.Name
                });
            return SpeechResult.Failed("Couldn't start Piper reading.");
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                CleanupPlaybackStateUnsafe();
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

            CancelActivePlaybackUnsafe();
            IsSpeaking = false;
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "piper_speech_stop_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return SpeechResult.Failed("Couldn't stop Piper reading.");
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
        CleanupPlaybackStateUnsafe();
        _playbackCancellationTokenSource?.Dispose();
        DisposeWarmPiperProcessUnsafe();
        _gate.Dispose();
        _disposed = true;
    }

    private async Task<SpeechResult> PlayRenderedAudioAsync(string text, SpeechOptions options, PiperVoiceDefinition voice, CancellationToken cancellationToken)
    {
        var waveBytes = await RenderWaveBytesAsync(text, options, voice, cancellationToken).ConfigureAwait(false);
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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastPlaybackCompletedUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }

        return SpeechResult.Completed();
    }

    private async Task<byte[]> RenderWaveBytesAsync(string text, SpeechOptions options, PiperVoiceDefinition voice, CancellationToken cancellationToken)
    {
        Process process;
        StreamWriter inputWriter;
        string outputDirectory;
        HashSet<string> existingFiles;
        var requestedLengthScale = MapLengthScale(options.Rate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureWarmPiperProcessUnsafe(voice, requestedLengthScale);
            process = _warmPiperProcess!;
            inputWriter = _warmPiperInputWriter!;
            outputDirectory = _warmPiperOutputDirectory!;
            _currentPiperProcess = process;
            existingFiles = Directory.EnumerateFiles(outputDirectory, "*.wav", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await inputWriter.WriteLineAsync(text).ConfigureAwait(false);
            await inputWriter.FlushAsync().ConfigureAwait(false);

            var outputFilePath = await WaitForNextOutputFileAsync(outputDirectory, existingFiles, process, cancellationToken).ConfigureAwait(false);
            var waveBytes = await ReadWaveWithRetryAsync(outputFilePath, cancellationToken).ConfigureAwait(false);
            TryDeleteFile(outputFilePath);

            var primerSeconds = ResolvePiperPrimerSeconds(text, options);
            return SpeechAudioHelper.PrependPrimerWave(waveBytes, primerSeconds);
        }
        catch
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_currentPiperProcess, _warmPiperProcess))
                {
                    DisposeWarmPiperProcessUnsafe();
                }
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_currentPiperProcess, process))
                {
                    _currentPiperProcess = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private string BuildArguments(PiperVoiceDefinition voice, string outputDirectory, double lengthScale)
    {
        var arguments = new List<string>
        {
            "--model",
            QuoteArgument(voice.ModelPath),
            "--output_dir",
            QuoteArgument(outputDirectory),
            "--length_scale",
            lengthScale.ToString("0.00", CultureInfo.InvariantCulture),
            "--quiet"
        };

        if (!string.IsNullOrWhiteSpace(voice.ConfigPath))
        {
            arguments.Add("--config");
            arguments.Add(QuoteArgument(voice.ConfigPath));
        }

        return string.Join(" ", arguments);
    }

    private async Task<string> WaitForNextOutputFileAsync(
        string outputDirectory,
        HashSet<string> existingFiles,
        Process process,
        CancellationToken cancellationToken)
    {
        var deadlineUtc = DateTime.UtcNow.AddMilliseconds(ReusedPiperOutputTimeoutMilliseconds);
        while (DateTime.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException($"Piper exited unexpectedly with code {process.ExitCode}.");
            }

            foreach (var filePath in Directory.EnumerateFiles(outputDirectory, "*.wav", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(filePath);
                if (string.IsNullOrWhiteSpace(fileName) || existingFiles.Contains(fileName))
                {
                    continue;
                }

                return filePath;
            }

            await Task.Delay(ReusedPiperOutputPollMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Piper timed out before producing output.");
    }

    private static async Task<byte[]> ReadWaveWithRetryAsync(string filePath, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < ReusedPiperReadRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > 0)
                {
                    var bytes = new byte[stream.Length];
                    var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                    if (read == bytes.Length)
                    {
                        return bytes;
                    }
                }
            }
            catch (IOException)
            {
                // File may still be finishing writes.
            }

            await Task.Delay(ReusedPiperReadRetryMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Piper output wave file wasn't readable.");
    }

    private PiperVoiceDefinition? ResolveVoice(string? voiceName)
    {
        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            var match = _voices.FirstOrDefault(voice => string.Equals(voice.Name, voiceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        foreach (var preferredVoice in PreferredDefaultVoices)
        {
            var match = _voices.FirstOrDefault(voice => string.Equals(voice.Name, preferredVoice, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return _voices
            .OrderBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? LocatePiperExecutable()
    {
        foreach (var candidate in EnumeratePiperExecutableCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private IEnumerable<PiperVoiceDefinition> DiscoverVoices()
    {
        if (string.IsNullOrWhiteSpace(_piperExecutablePath))
        {
            yield break;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var voicesDirectory in EnumerateVoiceDirectoryCandidates())
        {
            if (!Directory.Exists(voicesDirectory))
            {
                continue;
            }

            foreach (var modelPath in Directory.EnumerateFiles(voicesDirectory, "*.onnx", SearchOption.AllDirectories))
            {
                var configPath = $"{modelPath}.json";
                if (!File.Exists(configPath))
                {
                    continue;
                }

                var modelName = Path.GetFileNameWithoutExtension(modelPath);
                var voiceName = $"{PiperVoicePrefix}{modelName}";
                if (!seenNames.Add(voiceName))
                {
                    continue;
                }

                yield return new PiperVoiceDefinition(
                    voiceName,
                    BuildDisplayName(modelName),
                    modelPath,
                    configPath);
            }
        }
    }

    private static IEnumerable<string> EnumeratePiperExecutableCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(localAppData, "RightSpeak", "Piper", "piper.exe");
        yield return Path.Combine(localAppData, "RightSpeak", "Piper", "piper", "piper.exe");
        yield return Path.Combine(baseDirectory, "Resources", "Piper", "piper.exe");
        yield return Path.Combine(baseDirectory, "Resources", "Piper", "piper", "piper.exe");
        yield return Path.Combine(baseDirectory, "Piper", "piper.exe");
        yield return Path.Combine(baseDirectory, "Piper", "piper", "piper.exe");
    }

    private static IEnumerable<string> EnumerateVoiceDirectoryCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDirectory = AppContext.BaseDirectory;

        yield return Path.Combine(localAppData, "RightSpeak", "Piper", "voices");
        yield return Path.Combine(localAppData, "RightSpeak", "Piper");
        yield return Path.Combine(baseDirectory, "Resources", "Piper", "voices");
        yield return Path.Combine(baseDirectory, "Resources", "Piper");
        yield return Path.Combine(baseDirectory, "Piper", "voices");
        yield return Path.Combine(baseDirectory, "Piper");
    }

    private static string BuildDisplayName(string modelName)
    {
        var parts = modelName.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            var locale = parts[0];
            var voice = parts[1];
            var quality = parts[2];

            return $"{FormatPiperVoiceName(voice)} ({FormatLocale(locale)}, {FormatQuality(quality)})";
        }

        return modelName
            .Replace('_', ' ')
            .Replace('-', ' ');
    }

    private void CancelActivePlaybackUnsafe()
    {
        _playbackCancellationTokenSource?.Cancel();
        _currentSoundPlayer?.Stop();

        if (_currentPiperProcess is null)
        {
            return;
        }

        if (ReferenceEquals(_currentPiperProcess, _warmPiperProcess))
        {
            DisposeWarmPiperProcessUnsafe();
            _currentPiperProcess = null;
            return;
        }

        TryKillProcess(_currentPiperProcess);
        _currentPiperProcess.Dispose();
        _currentPiperProcess = null;
    }

    private void CleanupPlaybackStateUnsafe()
    {
        _currentSoundPlayer?.Stop();
        _currentSoundPlayer?.Dispose();
        _currentSoundPlayer = null;
        CancelActivePlaybackUnsafe();

        _playbackCancellationTokenSource?.Dispose();
        _playbackCancellationTokenSource = null;
    }

    private void EnsureWarmPiperProcessUnsafe(PiperVoiceDefinition voice, double lengthScale)
    {
        var shouldRestart =
            _warmPiperProcess is null ||
            _warmPiperProcess.HasExited ||
            _warmPiperInputWriter is null ||
            string.IsNullOrWhiteSpace(_warmPiperOutputDirectory) ||
            !string.Equals(_warmPiperModelPath, voice.ModelPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_warmPiperConfigPath, voice.ConfigPath, StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(_warmPiperLengthScale - lengthScale) > 0.001;

        if (!shouldRestart)
        {
            return;
        }

        DisposeWarmPiperProcessUnsafe();

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"rightspeak-piper-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _piperExecutablePath!,
                Arguments = BuildArguments(voice, outputDirectory, lengthScale),
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Piper process did not start.");
        }

        _warmPiperProcess = process;
        _warmPiperInputWriter = process.StandardInput;
        _warmPiperOutputDirectory = outputDirectory;
        _warmPiperModelPath = voice.ModelPath;
        _warmPiperConfigPath = voice.ConfigPath;
        _warmPiperLengthScale = lengthScale;
    }

    private void DisposeWarmPiperProcessUnsafe()
    {
        TryKillProcess(_warmPiperProcess);
        _warmPiperInputWriter?.Dispose();
        _warmPiperProcess?.Dispose();
        _warmPiperProcess = null;
        _warmPiperInputWriter = null;
        _warmPiperModelPath = null;
        _warmPiperConfigPath = null;
        _warmPiperLengthScale = 0;

        if (!string.IsNullOrWhiteSpace(_warmPiperOutputDirectory))
        {
            TryDeleteDirectory(_warmPiperOutputDirectory);
            _warmPiperOutputDirectory = null;
        }
    }

    private double ResolvePiperPrimerSeconds(string text, SpeechOptions options)
    {
        if (options.LeadingPrimerSecondsOverride is double overrideSeconds)
        {
            return Math.Clamp(overrideSeconds, PiperPrimerMinSeconds, PiperPrimerMaxSeconds);
        }

        if (text.Length >= 800)
        {
            return PiperPrimerLongTextSeconds;
        }

        var idleDuration = DateTime.UtcNow - _lastPlaybackCompletedUtc;
        if (_lastPlaybackCompletedUtc == DateTime.MinValue || idleDuration.TotalSeconds >= WarmSessionIdleResetSeconds)
        {
            return PiperPrimerColdStartSeconds;
        }

        return PiperPrimerWarmStartSeconds;
    }

    private static void TryKillProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            AppDiagnostics.Warn(
                "piper_temp_file_delete_failed",
                new Dictionary<string, string?>
                {
                    ["path"] = path
                });
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            AppDiagnostics.Warn(
                "piper_temp_directory_delete_failed",
                new Dictionary<string, string?>
                {
                    ["path"] = path
                });
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PiperSpeechService));
        }
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static double MapLengthScale(int rate)
    {
        var mapped = 1.0 - (rate * 0.04);
        return Math.Clamp(mapped, 0.60, 1.40);
    }

    private static string FormatPiperVoiceName(string value)
    {
        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Select(CapitalizeToken));
    }

    private static string FormatLocale(string value)
    {
        var normalized = value.Replace('_', '-');
        return normalized switch
        {
            "en-US" => "US",
            "en-GB" => "UK",
            _ => normalized
        };
    }

    private static string FormatQuality(string value)
    {
        return value.Replace('_', ' ') switch
        {
            "x low" => "Extra Low",
            "low" => "Low",
            "medium" => "Medium",
            "high" => "High",
            var other => string.Join(" ", other.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(CapitalizeToken))
        };
    }

    private static string CapitalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length == 1
            ? value.ToUpperInvariant()
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private sealed record PiperVoiceDefinition(
        string Name,
        string DisplayName,
        string ModelPath,
        string? ConfigPath);

    private static PiperAvailability ProbeAvailability(string? executablePath, IReadOnlyList<PiperVoiceDefinition> voices)
    {
        if (Environment.Is64BitOperatingSystem && RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            var failureReason = $"Piper unavailable: current process architecture is {RuntimeInformation.ProcessArchitecture}.";
            LogAvailabilityFailure(failureReason);
            return PiperAvailability.Unavailable(failureReason);
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            const string failureReason = "Piper unavailable: piper.exe was not found.";
            LogAvailabilityFailure(failureReason);
            return PiperAvailability.Unavailable(failureReason);
        }

        var runtimeDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
        {
            const string failureReason = "Piper unavailable: runtime directory was not found.";
            LogAvailabilityFailure(failureReason);
            return PiperAvailability.Unavailable(failureReason);
        }

        var missingRuntimeDependencies = new List<string>();
        foreach (var dependency in new[]
                 {
                     "onnxruntime.dll",
                     "espeak-ng.dll",
                     "piper_phonemize.dll"
                 })
        {
            if (!File.Exists(Path.Combine(runtimeDirectory, dependency)))
            {
                missingRuntimeDependencies.Add(dependency);
            }
        }

        if (!Directory.Exists(Path.Combine(runtimeDirectory, "espeak-ng-data")))
        {
            missingRuntimeDependencies.Add("espeak-ng-data");
        }

        if (missingRuntimeDependencies.Count > 0)
        {
            var failureReason = $"Piper unavailable: missing runtime dependencies ({string.Join(", ", missingRuntimeDependencies)}).";
            LogAvailabilityFailure(failureReason);
            return PiperAvailability.Unavailable(failureReason);
        }

        if (voices.Count == 0)
        {
            const string failureReason = "Piper unavailable: no valid Piper voice models were found.";
            LogAvailabilityFailure(failureReason);
            return PiperAvailability.Unavailable(failureReason);
        }

        var smokeTestResult = RunSmokeTest(executablePath, voices[0]);
        if (!smokeTestResult.IsAvailable)
        {
            LogAvailabilityFailure(smokeTestResult.FailureReason!);
            return PiperAvailability.Unavailable(smokeTestResult.FailureReason!);
        }

        AppDiagnostics.Info(
            "piper_available",
            new Dictionary<string, string?>
            {
                ["voiceCount"] = voices.Count.ToString(CultureInfo.InvariantCulture),
                ["executablePath"] = executablePath
            });

        return PiperAvailability.Available();
    }

    private static PiperAvailability RunSmokeTest(string executablePath, PiperVoiceDefinition voice)
    {
        var outputFilePath = Path.Combine(Path.GetTempPath(), $"rightspeak-piper-probe-{Guid.NewGuid():N}.wav");
        var arguments = string.Join(" ", new[]
        {
            "--model",
            QuoteArgument(voice.ModelPath),
            "--config",
            QuoteArgument(voice.ConfigPath ?? string.Empty),
            "--output_file",
            QuoteArgument(outputFilePath),
            "--length_scale",
            "1.00"
        });

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return PiperAvailability.Unavailable("Piper unavailable: smoke test process did not start.");
            }

            process.StandardInput.Write("RightSpeak Piper probe.");
            process.StandardInput.Close();

            if (!process.WaitForExit(15000))
            {
                TryKillProcess(process);
                return PiperAvailability.Unavailable("Piper unavailable: smoke test timed out.");
            }

            var standardError = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                var failureReason = string.IsNullOrWhiteSpace(standardError)
                    ? $"Piper unavailable: smoke test failed with exit code {process.ExitCode}."
                    : $"Piper unavailable: {standardError.Trim()}";
                return PiperAvailability.Unavailable(failureReason);
            }

            if (!File.Exists(outputFilePath))
            {
                return PiperAvailability.Unavailable("Piper unavailable: smoke test produced no wave output.");
            }

            var outputInfo = new FileInfo(outputFilePath);
            if (outputInfo.Length == 0)
            {
                return PiperAvailability.Unavailable("Piper unavailable: smoke test produced an empty wave output.");
            }

            return PiperAvailability.Available();
        }
        catch (Exception ex)
        {
            return PiperAvailability.Unavailable($"Piper unavailable: {ex.Message}");
        }
        finally
        {
            TryDeleteFile(outputFilePath);
        }
    }

    private static void LogAvailabilityFailure(string failureReason)
    {
        AppDiagnostics.Warn(
            "piper_unavailable",
            new Dictionary<string, string?>
            {
                ["reason"] = failureReason
            });
    }

    private sealed record PiperAvailability(bool IsAvailable, string? FailureReason)
    {
        public static PiperAvailability Available()
        {
            return new PiperAvailability(true, null);
        }

        public static PiperAvailability Unavailable(string failureReason)
        {
            return new PiperAvailability(false, failureReason);
        }
    }
}

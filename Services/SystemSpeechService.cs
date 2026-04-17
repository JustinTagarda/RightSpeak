using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

internal sealed class SystemSpeechService : ISpeechService, IPrefetchSpeechService, IDisposable
{
    public sealed class SystemPrefetchedSpeechClip : IPrefetchedSpeechClip
    {
        private bool _disposed;

        internal SystemPrefetchedSpeechClip(byte[] waveBytes, int textLength)
        {
            WaveBytes = waveBytes;
            TextLength = textLength;
        }

        public string Engine => "System.Speech";
        public int TextLength { get; }
        internal byte[] WaveBytes { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            WaveBytes = Array.Empty<byte>();
            _disposed = true;
        }
    }

    private readonly SemaphoreSlim _gate;
    private readonly string[] _installedVoiceNames;
    private readonly string? _defaultVoiceName;
    private CancellationTokenSource? _playbackCancellationTokenSource;
    private CancellationTokenSource? _prefetchCancellationTokenSource;
    private SoundPlayer? _currentSoundPlayer;
    private bool _disposed;

    public SystemSpeechService()
    {
        using var voiceProbe = new SpeechSynthesizer();
        _gate = new SemaphoreSlim(1, 1);
        _installedVoiceNames = voiceProbe.GetInstalledVoices()
            .Select(voice => voice.VoiceInfo.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _defaultVoiceName = voiceProbe.Voice?.Name;
    }

    public bool IsSpeaking { get; private set; }

    public IReadOnlyList<SpeechVoice> GetInstalledVoices()
    {
        return _installedVoiceNames
            .Select(name => new SpeechVoice(name, name, "System.Speech"))
            .ToArray();
    }

    public bool SupportsVoice(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return false;
        }

        return _installedVoiceNames.Any(name => string.Equals(name, voiceName, StringComparison.OrdinalIgnoreCase));
    }

    public bool SupportsPrefetch(SpeechRequest request)
    {
        if (request is null)
        {
            return false;
        }

        var text = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(request.Options.VoiceName) || SupportsVoice(request.Options.VoiceName);
    }

    public async Task<IPrefetchedSpeechClip?> PrefetchAsync(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        if (!SupportsPrefetch(request))
        {
            return null;
        }

        CancellationTokenSource prefetchCancellationTokenSource;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            CancelPrefetchUnsafe();
            prefetchCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _prefetchCancellationTokenSource = prefetchCancellationTokenSource;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            var waveBytes = await Task.Run(
                    () => RenderWaveBytes(request.Text, request.Options, prefetchCancellationTokenSource.Token),
                    prefetchCancellationTokenSource.Token)
                .ConfigureAwait(false);
            return new SystemPrefetchedSpeechClip(waveBytes, request.Text.Length);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_prefetchCancellationTokenSource, prefetchCancellationTokenSource))
                {
                    _prefetchCancellationTokenSource?.Dispose();
                    _prefetchCancellationTokenSource = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task CancelPrefetchAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelPrefetchUnsafe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeechResult> SpeakPrefetchedAsync(
        IPrefetchedSpeechClip prefetchedClip,
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        if (prefetchedClip is not SystemPrefetchedSpeechClip systemClip)
        {
            return SpeechResult.Failed("Prefetched speech clip is not compatible with the active speech engine.");
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
            return await PlayWaveBytesAsync(systemClip.WaveBytes, request.Options, playbackCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "system_speech_prefetched_playback_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            return SpeechResult.Failed("Couldn't continue reading.");
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

            systemClip.Dispose();
        }
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
                "system_speech_playback_failed",
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
            CancelPrefetchUnsafe();

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
                "system_speech_stop_failed",
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
        CancelPrefetchUnsafe();
        _currentSoundPlayer?.Stop();
        _currentSoundPlayer?.Dispose();
        _playbackCancellationTokenSource?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }

    private async Task<SpeechResult> PlayRenderedAudioAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        var waveBytes = await Task.Run(() => RenderWaveBytes(text, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        return await PlayWaveBytesAsync(waveBytes, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeechResult> PlayWaveBytesAsync(byte[] waveBytes, SpeechOptions options, CancellationToken cancellationToken)
    {
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

    private byte[] RenderWaveBytes(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        using var synthesizer = new SpeechSynthesizer();
        using var waveStream = new MemoryStream();

        ApplyOptions(synthesizer, options);
        synthesizer.SetOutputToWaveStream(waveStream);
        synthesizer.Speak(text);

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
        synthesizer.Rate = Math.Clamp(options.Rate, -10, 10);

        if (string.IsNullOrWhiteSpace(options.VoiceName))
        {
            if (!string.IsNullOrWhiteSpace(_defaultVoiceName) &&
                !string.Equals(synthesizer.Voice?.Name, _defaultVoiceName, StringComparison.OrdinalIgnoreCase))
            {
                synthesizer.SelectVoice(_defaultVoiceName);
            }

            return;
        }

        var match = _installedVoiceNames
            .FirstOrDefault(name => string.Equals(name, options.VoiceName, StringComparison.OrdinalIgnoreCase));

        if (match is not null &&
            !string.Equals(synthesizer.Voice?.Name, match, StringComparison.OrdinalIgnoreCase))
        {
            synthesizer.SelectVoice(match);
        }
    }

    private void CancelPlaybackUnsafe()
    {
        _playbackCancellationTokenSource?.Cancel();
        _currentSoundPlayer?.Stop();
    }

    private void CancelPrefetchUnsafe()
    {
        _prefetchCancellationTokenSource?.Cancel();
        _prefetchCancellationTokenSource?.Dispose();
        _prefetchCancellationTokenSource = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SystemSpeechService));
        }
    }
}

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

internal sealed class WindowsNeuralSpeechService : ISpeechService, IPrefetchSpeechService, IDisposable
{
    public sealed class WindowsNeuralPrefetchedSpeechClip : IPrefetchedSpeechClip
    {
        private bool _disposed;

        internal WindowsNeuralPrefetchedSpeechClip(byte[] waveBytes, int textLength)
        {
            WaveBytes = waveBytes;
            TextLength = textLength;
        }

        public string Engine => "Windows OneCore";
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
    private readonly VoiceInformation[] _installedVoices;
    private readonly string[] _installedVoiceNames;
    private readonly string? _defaultVoiceName;
    private CancellationTokenSource? _playbackCancellationTokenSource;
    private CancellationTokenSource? _prefetchCancellationTokenSource;
    private ContinuousWaveOutPlayer? _currentContinuousPlaybackPlayer;
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
    public bool IsPaused { get; private set; }

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
            IsPaused = false;
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
                _playbackCancellationTokenSource?.Dispose();
                _playbackCancellationTokenSource = null;
                IsSpeaking = false;
                IsPaused = false;
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
            IsPaused = false;
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
        CancelPrefetchUnsafe();
        _currentContinuousPlaybackPlayer?.Stop();
        _currentContinuousPlaybackPlayer?.Dispose();
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
            var waveBytes = await RenderWaveBytesAsync(request.Text, request.Options, prefetchCancellationTokenSource.Token).ConfigureAwait(false);
            return new WindowsNeuralPrefetchedSpeechClip(waveBytes, request.Text.Length);
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
        if (prefetchedClip is not WindowsNeuralPrefetchedSpeechClip neuralClip)
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
            IsPaused = false;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await PlayWaveBytesAsync(neuralClip.WaveBytes, request.Options, playbackCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SpeechResult.Stopped();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "windows_neural_prefetched_playback_failed",
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
                _playbackCancellationTokenSource?.Dispose();
                _playbackCancellationTokenSource = null;
                IsSpeaking = false;
                IsPaused = false;
            }
            finally
            {
                _gate.Release();
            }

            neuralClip.Dispose();
        }
    }

    private async Task<SpeechResult> PlayRenderedAudioAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        var waveBytes = await RenderWaveBytesAsync(text, options, cancellationToken).ConfigureAwait(false);
        return await PlayWaveBytesAsync(waveBytes, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SpeechResult> PlayWaveBytesAsync(byte[] waveBytes, SpeechOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _ = options;
        var playbackId = Guid.NewGuid().ToString("N");
        using var playbackPlayer = new ContinuousWaveOutPlayer(playbackId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _currentContinuousPlaybackPlayer = playbackPlayer;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await playbackPlayer.EnqueueWaveAsync(waveBytes, cancellationToken).ConfigureAwait(false);
            await playbackPlayer.DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_currentContinuousPlaybackPlayer, playbackPlayer))
                {
                    _currentContinuousPlaybackPlayer = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

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
        _currentContinuousPlaybackPlayer?.Stop();
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

    public async Task<SpeechResult> PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsSpeaking)
            {
                return SpeechResult.Completed("Speech is already stopped.");
            }

            if (IsPaused)
            {
                return SpeechResult.Completed("Reading is already paused.");
            }

            _currentContinuousPlaybackPlayer?.Pause();
            IsPaused = true;
            return SpeechResult.Completed("Reading paused.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SpeechResult> ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!IsSpeaking)
            {
                return SpeechResult.Failed("Nothing is paused right now.");
            }

            if (!IsPaused)
            {
                return SpeechResult.Completed("Reading is already playing.");
            }

            _currentContinuousPlaybackPlayer?.Resume();
            IsPaused = false;
            return SpeechResult.Completed("Reading resumed.");
        }
        finally
        {
            _gate.Release();
        }
    }
}

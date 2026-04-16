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

public sealed class WindowsSpeechService : ISpeechService, IDisposable
{
    // Validated on a real target machine: some local audio paths clip the start of
    // each utterance. This leading silence is intentional and must not be removed
    // without repeated manual playback regression testing.
    private const double LeadingSilenceSeconds = 0.85;

    private readonly SemaphoreSlim _gate;
    private readonly string[] _installedVoiceNames;
    private readonly string? _defaultVoiceName;
    private CancellationTokenSource? _playbackCancellationTokenSource;
    private SoundPlayer? _currentSoundPlayer;
    private bool _disposed;

    public WindowsSpeechService()
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

    public IReadOnlyList<string> GetInstalledVoiceNames() => _installedVoiceNames;

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
            return SpeechResult.Failed($"Speech failed: {ex.Message}");
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
            return SpeechResult.Failed($"Stop failed: {ex.Message}");
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

    private async Task<SpeechResult> PlayRenderedAudioAsync(string text, SpeechOptions options, CancellationToken cancellationToken)
    {
        var waveBytes = await Task.Run(() => RenderWaveBytes(text, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var playbackDuration = GetPlaybackDuration(waveBytes);
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
        return PrependPrimerWave(waveStream.ToArray());
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

    private byte[] PrependPrimerWave(byte[] speechWaveBytes)
    {
        if (!TryReadWave(speechWaveBytes, out var format, out var speechPcmBytes))
        {
            return speechWaveBytes;
        }

        var leadingSilencePcmBytes = BuildLeadingSilencePcm(format);
        if (leadingSilencePcmBytes.Length == 0)
        {
            return speechWaveBytes;
        }

        var combinedPcmBytes = new byte[leadingSilencePcmBytes.Length + speechPcmBytes.Length];
        Buffer.BlockCopy(leadingSilencePcmBytes, 0, combinedPcmBytes, 0, leadingSilencePcmBytes.Length);
        Buffer.BlockCopy(speechPcmBytes, 0, combinedPcmBytes, leadingSilencePcmBytes.Length, speechPcmBytes.Length);
        return BuildWave(format, combinedPcmBytes);
    }

    private static bool TryReadWave(byte[] waveBytes, out WaveFormat format, out byte[] pcmBytes)
    {
        format = default;
        pcmBytes = Array.Empty<byte>();

        try
        {
            using var stream = new MemoryStream(waveBytes, writable: false);
            using var reader = new BinaryReader(stream);

            if (new string(reader.ReadChars(4)) != "RIFF")
            {
                return false;
            }

            _ = reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE")
            {
                return false;
            }

            byte[]? fmtChunk = null;
            byte[]? dataChunk = null;

            while (stream.Position < stream.Length)
            {
                var chunkId = new string(reader.ReadChars(4));
                var chunkSize = reader.ReadInt32();
                var chunkBytes = reader.ReadBytes(chunkSize);

                if ((chunkSize & 1) == 1 && stream.Position < stream.Length)
                {
                    stream.Position += 1;
                }

                if (chunkId == "fmt ")
                {
                    fmtChunk = chunkBytes;
                }
                else if (chunkId == "data")
                {
                    dataChunk = chunkBytes;
                }
            }

            if (fmtChunk is null || dataChunk is null || fmtChunk.Length < 16)
            {
                return false;
            }

            using var fmtStream = new MemoryStream(fmtChunk, writable: false);
            using var fmtReader = new BinaryReader(fmtStream);
            format = new WaveFormat(
                AudioFormat: fmtReader.ReadInt16(),
                ChannelCount: fmtReader.ReadInt16(),
                SampleRate: fmtReader.ReadInt32(),
                ByteRate: fmtReader.ReadInt32(),
                BlockAlign: fmtReader.ReadInt16(),
                BitsPerSample: fmtReader.ReadInt16());
            pcmBytes = dataChunk;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildLeadingSilencePcm(WaveFormat format)
    {
        if (format.AudioFormat != 1 || format.BlockAlign <= 0 || format.SampleRate <= 0)
        {
            return Array.Empty<byte>();
        }

        var sampleCount = (int)(format.SampleRate * LeadingSilenceSeconds);
        return new byte[sampleCount * format.BlockAlign];
    }

    private static byte[] BuildWave(WaveFormat format, byte[] pcmBytes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + pcmBytes.Length);
        writer.Write("WAVE".ToCharArray());
        writer.Write("fmt ".ToCharArray());
        writer.Write(16);
        writer.Write(format.AudioFormat);
        writer.Write(format.ChannelCount);
        writer.Write(format.SampleRate);
        writer.Write(format.ByteRate);
        writer.Write(format.BlockAlign);
        writer.Write(format.BitsPerSample);
        writer.Write("data".ToCharArray());
        writer.Write(pcmBytes.Length);
        writer.Write(pcmBytes);
        writer.Flush();
        return stream.ToArray();
    }

    private static TimeSpan GetPlaybackDuration(byte[] waveBytes)
    {
        if (TryReadWave(waveBytes, out var format, out var pcmBytes) &&
            format.ByteRate > 0 &&
            pcmBytes.Length > 0)
        {
            var seconds = (double)pcmBytes.Length / format.ByteRate;
            return TimeSpan.FromMilliseconds(Math.Ceiling((seconds * 1000) + 150));
        }

        return TimeSpan.FromSeconds(2);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsSpeechService));
        }
    }

    private readonly record struct WaveFormat(
        short AudioFormat,
        short ChannelCount,
        int SampleRate,
        int ByteRate,
        short BlockAlign,
        short BitsPerSample);
}

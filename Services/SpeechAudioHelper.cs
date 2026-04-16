using System;
using System.IO;

namespace RightSpeak.Services;

internal static class SpeechAudioHelper
{
    // Validated on a real target machine: some local audio paths clip the start of
    // each utterance. This leading silence is intentional and must not be removed
    // without repeated manual playback regression testing.
    private const double DefaultLeadingSilenceSeconds = 0.85;

    public static byte[] PrependPrimerWave(byte[] speechWaveBytes)
    {
        return PrependPrimerWave(speechWaveBytes, DefaultLeadingSilenceSeconds);
    }

    public static byte[] PrependPrimerWave(byte[] speechWaveBytes, double leadingSilenceSeconds)
    {
        if (!TryReadWave(speechWaveBytes, out var format, out var speechPcmBytes))
        {
            return speechWaveBytes;
        }

        var leadingSilencePcmBytes = BuildLeadingSilencePcm(format, leadingSilenceSeconds);
        if (leadingSilencePcmBytes.Length == 0)
        {
            return speechWaveBytes;
        }

        var combinedPcmBytes = new byte[leadingSilencePcmBytes.Length + speechPcmBytes.Length];
        Buffer.BlockCopy(leadingSilencePcmBytes, 0, combinedPcmBytes, 0, leadingSilencePcmBytes.Length);
        Buffer.BlockCopy(speechPcmBytes, 0, combinedPcmBytes, leadingSilencePcmBytes.Length, speechPcmBytes.Length);
        return BuildWave(format, combinedPcmBytes);
    }

    public static TimeSpan GetPlaybackDuration(byte[] waveBytes)
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

    private static byte[] BuildLeadingSilencePcm(WaveFormat format, double leadingSilenceSeconds)
    {
        if (leadingSilenceSeconds <= 0 ||
            format.AudioFormat != 1 ||
            format.BlockAlign <= 0 ||
            format.SampleRate <= 0)
        {
            return Array.Empty<byte>();
        }

        var sampleCount = (int)Math.Ceiling(format.SampleRate * leadingSilenceSeconds);
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

    private readonly record struct WaveFormat(
        short AudioFormat,
        short ChannelCount,
        int SampleRate,
        int ByteRate,
        short BlockAlign,
        short BitsPerSample);
}

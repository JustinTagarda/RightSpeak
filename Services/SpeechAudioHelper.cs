using System;
using System.IO;

namespace RightSpeak.Services;

internal static class SpeechAudioHelper
{
    // Validated on a real target machine: some local audio paths clip the start of
    // each utterance. This leading silence is intentional and must not be removed
    // without repeated manual playback regression testing.
    private const double DefaultLeadingSilenceSeconds = 0.85;
    private const double WarmupCarrierFrequencyHz = 220.0;
    private const double WarmupCarrierAmplitudeNormalized = 0.0080;

    public static byte[] PrependPrimerWave(byte[] speechWaveBytes)
    {
        return PrependPrimerWave(speechWaveBytes, DefaultLeadingSilenceSeconds);
    }

    public static byte[] PrependPrimerWave(byte[] speechWaveBytes, double leadingSilenceSeconds)
    {
        return PrependPrimerWave(speechWaveBytes, leadingSilenceSeconds, includeWarmupCarrier: false);
    }

    public static byte[] PrependPrimerWave(byte[] speechWaveBytes, double leadingSilenceSeconds, bool includeWarmupCarrier)
    {
        if (!TryReadWave(speechWaveBytes, out var format, out var speechPcmBytes))
        {
            return speechWaveBytes;
        }

        var leadingPrimerPcmBytes = BuildLeadingPrimerPcm(format, leadingSilenceSeconds, includeWarmupCarrier);
        if (leadingPrimerPcmBytes.Length == 0)
        {
            return speechWaveBytes;
        }

        var combinedPcmBytes = new byte[leadingPrimerPcmBytes.Length + speechPcmBytes.Length];
        Buffer.BlockCopy(leadingPrimerPcmBytes, 0, combinedPcmBytes, 0, leadingPrimerPcmBytes.Length);
        Buffer.BlockCopy(speechPcmBytes, 0, combinedPcmBytes, leadingPrimerPcmBytes.Length, speechPcmBytes.Length);
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

    public static byte[] CreateSilenceWaveLike(byte[] referenceWaveBytes, double silenceSeconds)
    {
        if (!TryReadWave(referenceWaveBytes, out var format, out _))
        {
            return Array.Empty<byte>();
        }

        var silencePcm = BuildLeadingSilencePcm(format, silenceSeconds);
        if (silencePcm.Length == 0)
        {
            return Array.Empty<byte>();
        }

        return BuildWave(format, silencePcm);
    }

    public static byte[] CreateWarmupCarrierWaveLike(byte[] referenceWaveBytes, double carrierSeconds)
    {
        if (!TryReadWave(referenceWaveBytes, out var format, out _))
        {
            return Array.Empty<byte>();
        }

        var carrierPcm = BuildWarmupCarrierPcm(format, carrierSeconds);
        if (carrierPcm.Length == 0)
        {
            return Array.Empty<byte>();
        }

        return BuildWave(format, carrierPcm);
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

    private static byte[] BuildLeadingPrimerPcm(WaveFormat format, double leadingSilenceSeconds, bool includeWarmupCarrier)
    {
        if (!includeWarmupCarrier)
        {
            return BuildLeadingSilencePcm(format, leadingSilenceSeconds);
        }

        // Keep a low-level non-silent carrier across the entire primer window to
        // avoid endpoint/silence trimming paths that can still consume first words.
        return BuildWarmupCarrierPcm(format, leadingSilenceSeconds);
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

    private static byte[] BuildWarmupCarrierPcm(WaveFormat format, double warmupSeconds)
    {
        if (warmupSeconds <= 0 ||
            format.AudioFormat != 1 ||
            format.BlockAlign <= 0 ||
            format.SampleRate <= 0 ||
            format.ChannelCount <= 0)
        {
            return Array.Empty<byte>();
        }

        var sampleCount = (int)Math.Ceiling(format.SampleRate * warmupSeconds);
        if (sampleCount <= 0)
        {
            return Array.Empty<byte>();
        }

        var bytes = new byte[sampleCount * format.BlockAlign];
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return Array.Empty<byte>();
        }

        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var progress = sampleCount == 1 ? 1.0 : (double)sampleIndex / (sampleCount - 1);
            var envelope = Math.Pow(1.0 - progress, 0.75);
            var waveform = Math.Sin((2 * Math.PI * WarmupCarrierFrequencyHz * sampleIndex / format.SampleRate) + (Math.PI / 2.0));
            var normalized = waveform * WarmupCarrierAmplitudeNormalized * envelope;

            for (var channel = 0; channel < format.ChannelCount; channel++)
            {
                var baseOffset = (sampleIndex * format.BlockAlign) + (channel * bytesPerSample);
                WritePcmSample(bytes, baseOffset, format.BitsPerSample, normalized);
            }
        }

        return bytes;
    }

    private static void WritePcmSample(byte[] destination, int offset, short bitsPerSample, double normalized)
    {
        normalized = Math.Clamp(normalized, -1.0, 1.0);

        switch (bitsPerSample)
        {
            case 8:
            {
                var value = 128 + (int)Math.Round(normalized * 127.0);
                destination[offset] = (byte)Math.Clamp(value, 0, 255);
                return;
            }
            case 16:
            {
                var value = (short)Math.Round(normalized * short.MaxValue);
                destination[offset] = (byte)(value & 0xFF);
                destination[offset + 1] = (byte)((value >> 8) & 0xFF);
                return;
            }
            case 24:
            {
                var value = (int)Math.Round(normalized * 8388607.0);
                destination[offset] = (byte)(value & 0xFF);
                destination[offset + 1] = (byte)((value >> 8) & 0xFF);
                destination[offset + 2] = (byte)((value >> 16) & 0xFF);
                return;
            }
            case 32:
            {
                var value = (int)Math.Round(normalized * int.MaxValue);
                destination[offset] = (byte)(value & 0xFF);
                destination[offset + 1] = (byte)((value >> 8) & 0xFF);
                destination[offset + 2] = (byte)((value >> 16) & 0xFF);
                destination[offset + 3] = (byte)((value >> 24) & 0xFF);
                return;
            }
        }
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

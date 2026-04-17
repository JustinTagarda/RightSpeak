using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services;

internal sealed class ContinuousWaveOutPlayer : IDisposable
{
    private const uint WaveMapperDeviceId = 0xFFFFFFFF;
    private const uint CallbackFunction = 0x00030000;
    private const uint WomDoneMessage = 0x03BD;
    private const int WaverrStillPlaying = 33;
    private const int DisposeCallbackWaitMilliseconds = 750;
    private const int UnprepareRetryDelayMilliseconds = 25;
    private const int UnprepareRetryCount = 20;

    private static readonly object AbandonedPlayersSync = new();
    private static readonly List<ContinuousWaveOutPlayer> AbandonedPlayers = new();
    private readonly object _sync = new();
    private readonly Dictionary<nint, QueuedBuffer> _queuedBuffers = new();
    private readonly List<QueuedBuffer> _completedBuffers = new();
    private readonly List<QueuedBuffer> _retainedBuffers = new();
    private readonly WaveOutProc _callback;
    private readonly string? _streamId;
    private TaskCompletionSource<bool> _drainedCompletionSource;
    private nint _waveOutHandle;
    private SpeechAudioHelper.WaveFormat? _openedFormat;
    private int _pendingBufferCount;
    private bool _stopRequested;
    private bool _resetIssued;
    private bool _disposed;

    public ContinuousWaveOutPlayer(string? streamId = null)
    {
        _streamId = streamId;
        _callback = OnWaveOutCallback;
        _drainedCompletionSource = CreateCompletedDrainSource();
    }

    public Task EnqueueWaveAsync(byte[] waveBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!SpeechAudioHelper.TryReadWaveData(waveBytes, out var waveData))
        {
            throw new InvalidOperationException("Continuous playback requires PCM wave data.");
        }

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_stopRequested)
            {
                throw new OperationCanceledException("Continuous playback was stopped.", cancellationToken);
            }

            EnsureOpenedUnsafe(waveData.Format);
            if (_pendingBufferCount == 0)
            {
                _drainedCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            AppDiagnostics.Info(
                "continuous_stream_buffer_enqueue_started",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["sampleRate"] = waveData.Format.SampleRate.ToString(),
                    ["channels"] = waveData.Format.ChannelCount.ToString(),
                    ["bitsPerSample"] = waveData.Format.BitsPerSample.ToString(),
                    ["bufferBytes"] = waveData.PcmBytes.Length.ToString(),
                    ["pendingBuffers"] = _pendingBufferCount.ToString(),
                    ["waveOutHandle"] = _waveOutHandle.ToString("X")
                });

            var buffer = new QueuedBuffer(waveData.PcmBytes);
            try
            {
                PrepareBufferUnsafe(buffer);
                _queuedBuffers.Add(buffer.HeaderPointer, buffer);
                _pendingBufferCount++;

                var writeResult = waveOutWrite(_waveOutHandle, buffer.HeaderPointer, buffer.HeaderSize);
                if (writeResult != 0)
                {
                    _queuedBuffers.Remove(buffer.HeaderPointer);
                    _pendingBufferCount--;
                    CleanupBuffer(buffer, _waveOutHandle);
                    ThrowWaveOutError("waveOutWrite", writeResult);
                }

                AppDiagnostics.Info(
                    "continuous_stream_buffer_enqueued",
                    new Dictionary<string, string?>
                    {
                        ["streamId"] = _streamId,
                        ["sampleRate"] = waveData.Format.SampleRate.ToString(),
                        ["channels"] = waveData.Format.ChannelCount.ToString(),
                        ["bitsPerSample"] = waveData.Format.BitsPerSample.ToString(),
                        ["bufferBytes"] = waveData.PcmBytes.Length.ToString(),
                        ["pendingBuffers"] = _pendingBufferCount.ToString(),
                        ["waveOutHandle"] = _waveOutHandle.ToString("X")
                    });
            }
            catch
            {
                buffer.Dispose();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        Task drainedTask;
        lock (_sync)
        {
            ThrowIfDisposed();
            drainedTask = _drainedCompletionSource.Task;

            AppDiagnostics.Info(
                "continuous_stream_drain_waiting",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["pendingBuffers"] = _pendingBufferCount.ToString(),
                    ["stopRequested"] = _stopRequested.ToString(),
                    ["waveOutHandle"] = _waveOutHandle.ToString("X")
                });
        }

        await drainedTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        AppDiagnostics.Info(
            "continuous_stream_drain_completed",
            new Dictionary<string, string?>
            {
                ["streamId"] = _streamId,
                ["pendingBuffers"] = _pendingBufferCount.ToString(),
                ["stopRequested"] = _stopRequested.ToString(),
                ["waveOutHandle"] = _waveOutHandle.ToString("X")
            });
    }

    public void Stop()
    {
        nint waveOutHandle;
        var shouldReset = false;
        lock (_sync)
        {
            if (_disposed || _stopRequested)
            {
                return;
            }

            _stopRequested = true;
            waveOutHandle = _waveOutHandle;
            shouldReset = waveOutHandle != nint.Zero && !_resetIssued;
            if (shouldReset)
            {
                _resetIssued = true;
            }

            AppDiagnostics.Info(
                "continuous_stream_stop_requested",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["pendingBuffers"] = _pendingBufferCount.ToString(),
                    ["waveOutHandle"] = waveOutHandle.ToString("X")
                });
        }

        if (shouldReset)
        {
            Reset(waveOutHandle);
        }

        lock (_sync)
        {
            _drainedCompletionSource.TrySetCanceled();

            AppDiagnostics.Info(
                "continuous_stream_stopped",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["pendingBuffers"] = _pendingBufferCount.ToString(),
                    ["queuedBuffers"] = _queuedBuffers.Count.ToString(),
                    ["completedBuffers"] = _completedBuffers.Count.ToString(),
                    ["waveOutHandle"] = _waveOutHandle.ToString("X")
                });
        }
    }

    public void Dispose()
    {
        nint waveOutHandle;
        var shouldReset = false;
        var pendingBeforeDispose = 0;
        var queuedBeforeDispose = 0;
        var completedBeforeDispose = 0;
        var buffersToRelease = new List<QueuedBuffer>();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _stopRequested = true;
            waveOutHandle = _waveOutHandle;
            shouldReset = waveOutHandle != nint.Zero && !_resetIssued;
            if (shouldReset)
            {
                _resetIssued = true;
            }
            pendingBeforeDispose = _pendingBufferCount;
            queuedBeforeDispose = _queuedBuffers.Count;
            completedBeforeDispose = _completedBuffers.Count;
            _drainedCompletionSource.TrySetCanceled();

            AppDiagnostics.Info(
                "continuous_stream_dispose_requested",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["pendingBuffers"] = pendingBeforeDispose.ToString(),
                    ["queuedBuffers"] = queuedBeforeDispose.ToString(),
                    ["completedBuffers"] = completedBeforeDispose.ToString(),
                    ["waveOutHandle"] = waveOutHandle.ToString("X")
                });
        }

        if (shouldReset)
        {
            Reset(waveOutHandle);
        }

        WaitForPendingCallbacks();
        buffersToRelease.AddRange(SnapshotAllBuffers());
        var unreleasedBuffers = CleanupBuffers(waveOutHandle, buffersToRelease);
        if (unreleasedBuffers.Count == 0)
        {
            Close(waveOutHandle);
        }
        else
        {
            RetainAbandonedPlaybackUnsafe(waveOutHandle, unreleasedBuffers);
        }

        lock (_sync)
        {
            if (unreleasedBuffers.Count == 0)
            {
                _waveOutHandle = nint.Zero;
                _openedFormat = null;
            }

            _pendingBufferCount = 0;
            _disposed = true;

            AppDiagnostics.Info(
                "continuous_stream_disposed",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["retainedBuffers"] = _retainedBuffers.Count.ToString(),
                    ["waveOutHandle"] = _waveOutHandle.ToString("X")
                });
        }
    }

    private void OnWaveOutCallback(nint waveOutHandle, uint message, nuint instance, nint param1, nint param2)
    {
        if (message != WomDoneMessage || param1 == nint.Zero)
        {
            return;
        }

        lock (_sync)
        {
            if (!_queuedBuffers.Remove(param1, out var buffer))
            {
                return;
            }

            _completedBuffers.Add(buffer);
            _pendingBufferCount = Math.Max(0, _pendingBufferCount - 1);
            AppDiagnostics.Info(
                "continuous_stream_buffer_completed",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["pendingBuffers"] = _pendingBufferCount.ToString(),
                    ["completedBuffers"] = _completedBuffers.Count.ToString(),
                    ["waveOutHandle"] = waveOutHandle.ToString("X")
                });
            if (_pendingBufferCount == 0)
            {
                _drainedCompletionSource.TrySetResult(true);
            }
        }
    }

    private void EnsureOpenedUnsafe(SpeechAudioHelper.WaveFormat format)
    {
        if (_waveOutHandle != nint.Zero)
        {
            if (_openedFormat is not null && _openedFormat.Value != format)
            {
                throw new InvalidOperationException("Chunk playback changed audio format mid-read.");
            }

            return;
        }

        var waveFormat = new WaveFormatEx(
            formatTag: format.AudioFormat,
            channels: format.ChannelCount,
            samplesPerSec: (uint)format.SampleRate,
            avgBytesPerSec: (uint)format.ByteRate,
            blockAlign: (ushort)format.BlockAlign,
            bitsPerSample: (ushort)format.BitsPerSample,
            size: 0);

        var openResult = waveOutOpen(
            out _waveOutHandle,
            WaveMapperDeviceId,
            ref waveFormat,
            _callback,
            nint.Zero,
            CallbackFunction);
        if (openResult != 0)
        {
            _waveOutHandle = nint.Zero;
            ThrowWaveOutError("waveOutOpen", openResult);
        }

        AppDiagnostics.Info(
            "continuous_stream_device_opened",
            new Dictionary<string, string?>
            {
                ["streamId"] = _streamId,
                ["sampleRate"] = format.SampleRate.ToString(),
                ["channels"] = format.ChannelCount.ToString(),
                ["bitsPerSample"] = format.BitsPerSample.ToString(),
                ["blockAlign"] = format.BlockAlign.ToString(),
                ["byteRate"] = format.ByteRate.ToString(),
                ["waveOutHandle"] = _waveOutHandle.ToString("X")
            });

        _openedFormat = format;
    }

    private void PrepareBufferUnsafe(QueuedBuffer buffer)
    {
        var header = new WaveHeader
        {
            Data = buffer.DataPointer,
            BufferLength = (uint)buffer.PcmBytes.Length,
            Flags = 0,
            Loops = 0,
            User = nint.Zero,
            Next = nint.Zero,
            Reserved = nint.Zero
        };

        Marshal.StructureToPtr(header, buffer.HeaderPointer, fDeleteOld: false);
        var prepareResult = waveOutPrepareHeader(_waveOutHandle, buffer.HeaderPointer, buffer.HeaderSize);
        if (prepareResult != 0)
        {
            ThrowWaveOutError("waveOutPrepareHeader", prepareResult);
        }

        buffer.MarkPrepared();
    }

    private List<QueuedBuffer> SnapshotAllBuffersUnsafe()
    {
        var allBuffers = new List<QueuedBuffer>(_queuedBuffers.Count + _completedBuffers.Count);
        allBuffers.AddRange(_queuedBuffers.Values);
        allBuffers.AddRange(_completedBuffers);
        _queuedBuffers.Clear();
        _completedBuffers.Clear();
        _pendingBufferCount = 0;
        return allBuffers;
    }

    private List<QueuedBuffer> SnapshotAllBuffers()
    {
        lock (_sync)
        {
            return SnapshotAllBuffersUnsafe();
        }
    }

    private List<QueuedBuffer> CleanupBuffers(nint waveOutHandle, IEnumerable<QueuedBuffer> buffers)
    {
        var unreleasedBuffers = new List<QueuedBuffer>();
        foreach (var buffer in buffers)
        {
            if (!CleanupBuffer(buffer, waveOutHandle))
            {
                unreleasedBuffers.Add(buffer);
            }
        }

        return unreleasedBuffers;
    }

    private bool CleanupBuffer(QueuedBuffer buffer, nint waveOutHandle)
    {
        int? unprepareResult = null;
        if (waveOutHandle != nint.Zero && buffer.IsPrepared)
        {
            for (var attempt = 0; attempt <= UnprepareRetryCount; attempt++)
            {
                unprepareResult = waveOutUnprepareHeader(waveOutHandle, buffer.HeaderPointer, buffer.HeaderSize);
                if (unprepareResult != WaverrStillPlaying)
                {
                    break;
                }

                Thread.Sleep(UnprepareRetryDelayMilliseconds);
            }
        }

        AppDiagnostics.Info(
            "continuous_stream_buffer_released",
            new Dictionary<string, string?>
            {
                ["streamId"] = _streamId,
                ["prepared"] = buffer.IsPrepared.ToString(),
                ["bufferBytes"] = buffer.PcmBytes.Length.ToString(),
                ["unprepareResult"] = unprepareResult?.ToString() ?? "n/a",
                ["waveOutHandle"] = waveOutHandle.ToString("X")
            });

        if (unprepareResult == WaverrStillPlaying)
        {
            AppDiagnostics.Warn(
                "continuous_stream_buffer_retained_still_playing",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["bufferBytes"] = buffer.PcmBytes.Length.ToString(),
                    ["waveOutHandle"] = waveOutHandle.ToString("X")
                });
            return false;
        }

        buffer.Dispose();
        return true;
    }

    private void Reset(nint waveOutHandle)
    {
        if (waveOutHandle != nint.Zero)
        {
            AppDiagnostics.Info(
                "continuous_stream_device_reset",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["waveOutHandle"] = waveOutHandle.ToString("X")
                });
            _ = waveOutReset(waveOutHandle);
        }
    }

    private void Close(nint waveOutHandle)
    {
        if (waveOutHandle == nint.Zero)
        {
            return;
        }

        AppDiagnostics.Info(
            "continuous_stream_device_closing",
            new Dictionary<string, string?>
            {
                ["streamId"] = _streamId,
                ["waveOutHandle"] = waveOutHandle.ToString("X")
            });

        var closeResult = waveOutClose(waveOutHandle);
        AppDiagnostics.Info(
            "continuous_stream_device_closed",
            new Dictionary<string, string?>
            {
                ["streamId"] = _streamId,
                ["waveOutHandle"] = waveOutHandle.ToString("X"),
                ["closeResult"] = closeResult.ToString(),
                ["closeResultMessage"] = closeResult == 0 ? "MMSYSERR_NOERROR" : GetWaveOutErrorMessage(closeResult)
            });
    }

    private void WaitForPendingCallbacks()
    {
        var deadlineUtc = DateTime.UtcNow.AddMilliseconds(DisposeCallbackWaitMilliseconds);
        while (DateTime.UtcNow < deadlineUtc)
        {
            lock (_sync)
            {
                if (_queuedBuffers.Count == 0)
                {
                    return;
                }
            }

            Thread.Sleep(UnprepareRetryDelayMilliseconds);
        }

        lock (_sync)
        {
            AppDiagnostics.Warn(
                "continuous_stream_callback_wait_timed_out",
                new Dictionary<string, string?>
                {
                    ["streamId"] = _streamId,
                    ["queuedBuffers"] = _queuedBuffers.Count.ToString(),
                    ["completedBuffers"] = _completedBuffers.Count.ToString(),
                    ["pendingBuffers"] = _pendingBufferCount.ToString(),
                    ["waitMs"] = DisposeCallbackWaitMilliseconds.ToString()
                });
        }
    }

    private void RetainAbandonedPlaybackUnsafe(nint waveOutHandle, List<QueuedBuffer> unreleasedBuffers)
    {
        lock (_sync)
        {
            _retainedBuffers.AddRange(unreleasedBuffers);
        }

        lock (AbandonedPlayersSync)
        {
            AbandonedPlayers.Add(this);
        }

        AppDiagnostics.Warn(
            "continuous_stream_playback_retained_for_safety",
            new Dictionary<string, string?>
            {
                ["streamId"] = _streamId,
                ["retainedBuffers"] = unreleasedBuffers.Count.ToString(),
                ["waveOutHandle"] = waveOutHandle.ToString("X"),
                ["reason"] = "driver_still_owns_buffers"
            });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ContinuousWaveOutPlayer));
        }
    }

    private static void ThrowWaveOutError(string operation, int result)
    {
        throw new InvalidOperationException($"{operation} failed: {GetWaveOutErrorMessage(result)}");
    }

    private static string GetWaveOutErrorMessage(int result)
    {
        var buffer = new char[256];
        var errorTextResult = waveOutGetErrorText(result, buffer, buffer.Length);
        if (errorTextResult == 0)
        {
            return new string(buffer).TrimEnd('\0', ' ');
        }

        return $"MMRESULT {result}.";
    }

    private static TaskCompletionSource<bool> CreateCompletedDrainSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult(true);
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public WaveFormatEx(
            short formatTag,
            short channels,
            uint samplesPerSec,
            uint avgBytesPerSec,
            ushort blockAlign,
            ushort bitsPerSample,
            ushort size)
        {
            FormatTag = formatTag;
            Channels = channels;
            SamplesPerSec = samplesPerSec;
            AvgBytesPerSec = avgBytesPerSec;
            BlockAlign = blockAlign;
            BitsPerSample = bitsPerSample;
            Size = size;
        }

        public short FormatTag;
        public short Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WaveOutProc(nint waveOutHandle, uint message, nuint instance, nint param1, nint param2);

    private sealed class QueuedBuffer : IDisposable
    {
        private GCHandle _dataHandle;
        private bool _disposed;

        public QueuedBuffer(byte[] pcmBytes)
        {
            PcmBytes = pcmBytes ?? throw new ArgumentNullException(nameof(pcmBytes));
            _dataHandle = GCHandle.Alloc(PcmBytes, GCHandleType.Pinned);
            DataPointer = _dataHandle.AddrOfPinnedObject();
            HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
            HeaderPointer = Marshal.AllocHGlobal((int)HeaderSize);
        }

        public byte[] PcmBytes { get; }
        public nint DataPointer { get; }
        public nint HeaderPointer { get; }
        public uint HeaderSize { get; }
        public bool IsPrepared { get; private set; }

        public void MarkPrepared()
        {
            IsPrepared = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (HeaderPointer != nint.Zero)
            {
                Marshal.FreeHGlobal(HeaderPointer);
            }

            if (_dataHandle.IsAllocated)
            {
                _dataHandle.Free();
            }

            _disposed = true;
        }
    }

    [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int waveOutOpen(
        out nint waveOutHandle,
        uint deviceId,
        ref WaveFormatEx format,
        WaveOutProc callback,
        nint instance,
        uint flags);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int waveOutPrepareHeader(nint waveOutHandle, nint waveHeader, uint waveHeaderSize);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int waveOutUnprepareHeader(nint waveOutHandle, nint waveHeader, uint waveHeaderSize);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int waveOutWrite(nint waveOutHandle, nint waveHeader, uint waveHeaderSize);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int waveOutReset(nint waveOutHandle);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int waveOutClose(nint waveOutHandle);

    [DllImport("winmm.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Auto)]
    private static extern int waveOutGetErrorText(int error, [Out] char[] errorText, int errorTextCount);
}

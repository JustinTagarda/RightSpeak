using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class WindowsNamedPipeContextReadIngressService : IContextReadIngressService
{
    public const string DefaultPipeName = "RightSpeak.ContextRead.v1";

    private readonly string _pipeName;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _runCts;
    private Task? _serverTask;
    private bool _disposed;

    public WindowsNamedPipeContextReadIngressService(string pipeName = DefaultPipeName)
    {
        _pipeName = pipeName;
    }

    public event EventHandler<string>? ReadRequested;

    public void Start()
    {
        ThrowIfDisposed();

        lock (_syncRoot)
        {
            if (_runCts is not null)
            {
                return;
            }

            _runCts = new CancellationTokenSource();
            _serverTask = RunServerLoopAsync(_runCts.Token);
        }
    }

    public void Stop()
    {
        Task? serverTask;
        lock (_syncRoot)
        {
            if (_runCts is null)
            {
                return;
            }

            _runCts.Cancel();
            _runCts.Dispose();
            _runCts = null;
            serverTask = _serverTask;
            _serverTask = null;
        }

        try
        {
            serverTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private async Task RunServerLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var inboundText = await ReadInboundTextAsync(server, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(inboundText))
                {
                    ReadRequested?.Invoke(this, inboundText);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                // Keep listening for the next request.
            }
        }
    }

    private static async Task<string?> ReadInboundTextAsync(Stream input, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String)
            {
                return textElement.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
            // Non-JSON payloads are treated as plain text.
        }

        return payload.Trim();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsNamedPipeContextReadIngressService));
        }
    }
}

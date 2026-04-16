using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.WindowsIntegration;

public static class WindowsNamedPipeContextReadClient
{
    public static async Task<(bool Success, string Message)> SendReadRequestAsync(
        string text,
        string pipeName = WindowsNamedPipeContextReadIngressService.DefaultPipeName,
        int connectTimeoutMilliseconds = 1500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, "No text was provided.");
        }

        try
        {
            await using var pipeClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await pipeClient.ConnectAsync(connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

            await using var writer = new StreamWriter(
                pipeClient,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);

            var payload = JsonSerializer.Serialize(new { text });
            await writer.WriteAsync(payload).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (true, "Context read request sent.");
        }
        catch (TimeoutException)
        {
            return (false, "RightSpeak is not running or did not accept the request in time.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to send context read request: {ex.Message}");
        }
    }
}

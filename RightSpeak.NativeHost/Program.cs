using System.IO.Pipes;
using System.Text;
using System.Text.Json;

const string PipeName = "RightSpeak.ContextRead.v1";

return await RunAsync();

static async Task<int> RunAsync()
{
    try
    {
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();

        while (true)
        {
            var requestJson = await ReadFrameAsync(input).ConfigureAwait(false);
            if (requestJson is null)
            {
                return 0;
            }

            var text = ExtractText(requestJson);
            if (string.IsNullOrWhiteSpace(text))
            {
                await WriteFrameAsync(output, JsonSerializer.Serialize(new
                {
                    success = false,
                    message = "Native messaging request did not contain text."
                })).ConfigureAwait(false);
                continue;
            }

            var sendResult = await SendReadRequestAsync(text).ConfigureAwait(false);
            await WriteFrameAsync(output, JsonSerializer.Serialize(new
            {
                success = sendResult.Success,
                message = sendResult.Message
            })).ConfigureAwait(false);
        }
    }
    catch (Exception ex)
    {
        try
        {
            await using var output = Console.OpenStandardOutput();
            await WriteFrameAsync(output, JsonSerializer.Serialize(new
            {
                success = false,
                message = $"Native host failed: {ex.Message}"
            })).ConfigureAwait(false);
        }
        catch
        {
            // Ignore secondary failures while reporting host startup errors.
        }

        return 1;
    }
}

static async Task<(bool Success, string Message)> SendReadRequestAsync(
    string text,
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
            PipeName,
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

static string? ExtractText(string payload)
{
    try
    {
        using var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString()?.Trim();
        }
    }
    catch (JsonException)
    {
        // Fallback to plain payload text.
    }

    return payload.Trim();
}

static async Task<string?> ReadFrameAsync(Stream input)
{
    var lengthBuffer = new byte[4];
    var lengthRead = await ReadExactAsync(input, lengthBuffer, 0, 4).ConfigureAwait(false);
    if (lengthRead == 0)
    {
        return null;
    }

    if (lengthRead < 4)
    {
        throw new IOException("Incomplete native messaging frame length.");
    }

    var length = BitConverter.ToInt32(lengthBuffer, 0);
    if (length <= 0)
    {
        return null;
    }

    var payloadBuffer = new byte[length];
    var payloadRead = await ReadExactAsync(input, payloadBuffer, 0, length).ConfigureAwait(false);
    if (payloadRead < length)
    {
        throw new IOException("Incomplete native messaging payload.");
    }

    return Encoding.UTF8.GetString(payloadBuffer);
}

static async Task WriteFrameAsync(Stream output, string payload)
{
    var payloadBytes = Encoding.UTF8.GetBytes(payload);
    var lengthBytes = BitConverter.GetBytes(payloadBytes.Length);

    await output.WriteAsync(lengthBytes).ConfigureAwait(false);
    await output.WriteAsync(payloadBytes).ConfigureAwait(false);
    await output.FlushAsync().ConfigureAwait(false);
}

static async Task<int> ReadExactAsync(Stream input, byte[] buffer, int offset, int count)
{
    var totalRead = 0;
    while (totalRead < count)
    {
        var read = await input.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead)).ConfigureAwait(false);
        if (read == 0)
        {
            break;
        }

        totalRead += read;
    }

    return totalRead;
}

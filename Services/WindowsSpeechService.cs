using System;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class WindowsSpeechService : ISpeechService, IDisposable
{
    private readonly SpeechSynthesizer _speechSynthesizer;
    private readonly SemaphoreSlim _gate;
    private bool _disposed;

    public WindowsSpeechService()
    {
        _speechSynthesizer = new SpeechSynthesizer();
        _gate = new SemaphoreSlim(1, 1);
    }

    public bool IsSpeaking { get; private set; }

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

        TaskCompletionSource<SpeechResult> speakCompletion;
        EventHandler<SpeakCompletedEventArgs>? completed = null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (IsSpeaking)
            {
                _speechSynthesizer.SpeakAsyncCancelAll();
            }

            ApplyOptions(request.Options);

            speakCompletion = new TaskCompletionSource<SpeechResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            IsSpeaking = true;

            completed = (_, args) =>
            {
                _speechSynthesizer.SpeakCompleted -= completed;
                IsSpeaking = false;

                if (args.Error is not null)
                {
                    speakCompletion.TrySetResult(SpeechResult.Failed($"Speech failed: {args.Error.Message}"));
                    return;
                }

                speakCompletion.TrySetResult(args.Cancelled ? SpeechResult.Stopped() : SpeechResult.Completed());
            };

            _speechSynthesizer.SpeakCompleted += completed;
            _speechSynthesizer.SpeakAsync(text);
        }
        catch (Exception ex)
        {
            IsSpeaking = false;
            return SpeechResult.Failed($"Speech failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }

        using var registration = cancellationToken.Register(() => _speechSynthesizer.SpeakAsyncCancelAll());
        return await speakCompletion.Task.ConfigureAwait(false);
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

            _speechSynthesizer.SpeakAsyncCancelAll();
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

        _speechSynthesizer.SpeakAsyncCancelAll();
        _speechSynthesizer.Dispose();
        _gate.Dispose();
        _disposed = true;
    }

    private void ApplyOptions(SpeechOptions options)
    {
        _speechSynthesizer.Rate = Math.Clamp(options.Rate, -10, 10);

        if (string.IsNullOrWhiteSpace(options.VoiceName))
        {
            return;
        }

        var match = _speechSynthesizer.GetInstalledVoices()
            .Select(voice => voice.VoiceInfo.Name)
            .FirstOrDefault(name => string.Equals(name, options.VoiceName, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            _speechSynthesizer.SelectVoice(match);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsSpeechService));
        }
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public enum ReadingProgressStage
{
    Idle,
    Focusing,
    Retrieving,
    PreparingAudio,
    Speaking
}

public sealed record ReadingProgressUpdate(ReadingProgressStage Stage, string Message);

public interface IReadingService
{
    bool IsReading { get; }
    bool IsPaused { get; }
    IReadOnlyList<SpeechVoice> AvailableVoices { get; }
    int SpeechRate { get; set; }
    string? SelectedVoiceName { get; set; }
    string TypedTextDraft { get; set; }

    void RefreshAvailableVoices();

    Task<SpeechResult> ReadTextAsync(string text, CancellationToken cancellationToken = default);

    Task<SpeechResult> ReadSelectedTextAsync(
        CancellationToken cancellationToken = default,
        IProgress<ReadingProgressUpdate>? progress = null);
    Task<SpeechResult> ReadParagraphAsync(CancellationToken cancellationToken = default);
    Task<SpeechResult> ReadDocumentAsync(
        CancellationToken cancellationToken = default,
        IProgress<ReadingProgressUpdate>? progress = null);

    Task<SpeechResult> PauseAsync(CancellationToken cancellationToken = default);
    Task<SpeechResult> ResumeAsync(CancellationToken cancellationToken = default);
    Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default);
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IReadingService
{
    bool IsReading { get; }
    IReadOnlyList<SpeechVoice> AvailableVoices { get; }
    int SpeechRate { get; set; }
    string? SelectedVoiceName { get; set; }
    string TypedTextDraft { get; set; }

    Task<SpeechResult> ReadTextAsync(string text, CancellationToken cancellationToken = default);

    Task<SpeechResult> ReadSelectedTextAsync(CancellationToken cancellationToken = default);
    Task<SpeechResult> ReadParagraphAsync(CancellationToken cancellationToken = default);
    Task<SpeechResult> ReadDocumentAsync(CancellationToken cancellationToken = default);

    Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default);
}

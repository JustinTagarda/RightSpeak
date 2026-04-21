using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface ISpeechService
{
    bool IsSpeaking { get; }
    bool IsPaused { get; }
    IReadOnlyList<SpeechVoice> GetInstalledVoices();

    Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken cancellationToken = default);

    Task<SpeechResult> PauseAsync(CancellationToken cancellationToken = default);
    Task<SpeechResult> ResumeAsync(CancellationToken cancellationToken = default);
    Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default);
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface ISpeechService
{
    bool IsSpeaking { get; }
    IReadOnlyList<string> GetInstalledVoiceNames();

    Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken cancellationToken = default);

    Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default);
}

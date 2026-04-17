using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IPrefetchSpeechService
{
    bool SupportsPrefetch(SpeechRequest request);

    Task<IPrefetchedSpeechClip?> PrefetchAsync(SpeechRequest request, CancellationToken cancellationToken = default);

    Task<SpeechResult> SpeakPrefetchedAsync(
        IPrefetchedSpeechClip prefetchedClip,
        SpeechRequest request,
        CancellationToken cancellationToken = default);
}

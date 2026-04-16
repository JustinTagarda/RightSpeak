using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IParagraphTextProvider
{
    Task<TextRetrievalResult> TryGetParagraphTextAsync(CancellationToken cancellationToken = default);
}

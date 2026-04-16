using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IDocumentTextProvider
{
    Task<TextRetrievalResult> TryGetDocumentTextAsync(CancellationToken cancellationToken = default);
}

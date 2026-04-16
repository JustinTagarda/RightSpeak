using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface ISelectedTextProvider
{
    TextRetrievalSource Source { get; }

    Task<TextRetrievalResult> TryGetSelectedTextAsync(CancellationToken cancellationToken = default);
}

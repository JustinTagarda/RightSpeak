using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IParagraphTextRetrievalService
{
    Task<TextRetrievalResult> RetrieveParagraphTextAsync(CancellationToken cancellationToken = default);
}

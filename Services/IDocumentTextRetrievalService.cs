using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IDocumentTextRetrievalService
{
    Task<TextRetrievalResult> RetrieveDocumentTextAsync(CancellationToken cancellationToken = default);
}

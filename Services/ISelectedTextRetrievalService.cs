using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface ISelectedTextRetrievalService
{
    Task<TextRetrievalResult> RetrieveSelectedTextAsync(CancellationToken cancellationToken = default);
}

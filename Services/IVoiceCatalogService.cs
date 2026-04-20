using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public interface IVoiceCatalogService
{
    Task<IReadOnlyList<DownloadableVoice>> GetDownloadableVoicesAsync(CancellationToken cancellationToken = default);
}

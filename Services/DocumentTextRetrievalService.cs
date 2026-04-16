using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class DocumentTextRetrievalService : IDocumentTextRetrievalService
{
    private readonly IReadOnlyList<IDocumentTextProvider> _providers;

    public DocumentTextRetrievalService(IReadOnlyList<IDocumentTextProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<TextRetrievalResult> RetrieveDocumentTextAsync(CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0)
        {
            return TextRetrievalResult.Failed("No document-text providers are configured.");
        }

        TextRetrievalResult? lastFailure = null;

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await provider.TryGetDocumentTextAsync(cancellationToken).ConfigureAwait(false);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                return result;
            }

            lastFailure = result;
        }

        return lastFailure ?? TextRetrievalResult.Failed("Document-text retrieval is unavailable.");
    }
}

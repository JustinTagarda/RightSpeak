using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class ParagraphTextRetrievalService : IParagraphTextRetrievalService
{
    private readonly IReadOnlyList<IParagraphTextProvider> _providers;

    public ParagraphTextRetrievalService(IReadOnlyList<IParagraphTextProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<TextRetrievalResult> RetrieveParagraphTextAsync(CancellationToken cancellationToken = default)
    {
        if (_providers.Count == 0)
        {
            return TextRetrievalResult.Failed("No paragraph-text providers are configured.");
        }

        TextRetrievalResult? lastFailure = null;

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await provider.TryGetParagraphTextAsync(cancellationToken).ConfigureAwait(false);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
            {
                return result;
            }

            lastFailure = result;
        }

        return lastFailure ?? TextRetrievalResult.Failed("Paragraph-text retrieval is unavailable.");
    }
}

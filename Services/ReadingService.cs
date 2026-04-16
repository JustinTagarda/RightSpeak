using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class ReadingService : IReadingService
{
    private readonly ISpeechService _speechService;
    private readonly ISelectedTextRetrievalService _selectedTextRetrievalService;
    private readonly IParagraphTextRetrievalService _paragraphTextRetrievalService;
    private readonly IDocumentTextRetrievalService _documentTextRetrievalService;
    private readonly IAppSettingsService _settingsService;
    private readonly IReadOnlyList<string> _availableVoices;

    public ReadingService(
        ISpeechService speechService,
        ISelectedTextRetrievalService selectedTextRetrievalService,
        IParagraphTextRetrievalService paragraphTextRetrievalService,
        IDocumentTextRetrievalService documentTextRetrievalService,
        IAppSettingsService settingsService)
    {
        _speechService = speechService;
        _selectedTextRetrievalService = selectedTextRetrievalService;
        _paragraphTextRetrievalService = paragraphTextRetrievalService;
        _documentTextRetrievalService = documentTextRetrievalService;
        _settingsService = settingsService;
        _availableVoices = _speechService.GetInstalledVoiceNames();
        NormalizeSavedVoiceSetting();
    }

    public bool IsReading => _speechService.IsSpeaking;
    public IReadOnlyList<string> AvailableVoices => _availableVoices;
    public int SpeechRate
    {
        get => _settingsService.Current.SpeechRate;
        set
        {
            var clamped = value < -10 ? -10 : value > 10 ? 10 : value;
            if (_settingsService.Current.SpeechRate == clamped)
            {
                return;
            }

            _settingsService.Current.SpeechRate = clamped;
            _settingsService.Save();
        }
    }
    public string? SelectedVoiceName
    {
        get => NormalizeVoiceName(_settingsService.Current.VoiceName);
        set
        {
            var normalized = NormalizeVoiceName(value);
            if (string.Equals(_settingsService.Current.VoiceName, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Current.VoiceName = normalized;
            _settingsService.Save();
        }
    }

    public Task<SpeechResult> ReadTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(text);
        return _speechService.SpeakAsync(request, cancellationToken);
    }

    public async Task<SpeechResult> ReadSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        var retrieval = await _selectedTextRetrievalService.RetrieveSelectedTextAsync(cancellationToken).ConfigureAwait(false);
        if (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text))
        {
            return SpeechResult.Failed(retrieval.Message);
        }

        var request = BuildRequest(retrieval.Text);
        var speechResult = await _speechService.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    public Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default)
    {
        return _speechService.StopAsync(cancellationToken);
    }

    public async Task<SpeechResult> ReadParagraphAsync(CancellationToken cancellationToken = default)
    {
        var retrieval = await _paragraphTextRetrievalService.RetrieveParagraphTextAsync(cancellationToken).ConfigureAwait(false);
        if ((!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text)) && ShouldRetryParagraphRetrieval(retrieval))
        {
            await Task.Delay(220, cancellationToken).ConfigureAwait(false);
            retrieval = await _paragraphTextRetrievalService.RetrieveParagraphTextAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text))
        {
            return SpeechResult.Failed(retrieval.Message);
        }

        var request = BuildRequest(retrieval.Text);
        var speechResult = await _speechService.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    public async Task<SpeechResult> ReadDocumentAsync(CancellationToken cancellationToken = default)
    {
        var retrieval = await _documentTextRetrievalService.RetrieveDocumentTextAsync(cancellationToken).ConfigureAwait(false);
        if (!retrieval.Success || string.IsNullOrWhiteSpace(retrieval.Text))
        {
            return SpeechResult.Failed(retrieval.Message);
        }

        var request = BuildRequest(retrieval.Text);
        var speechResult = await _speechService.SpeakAsync(request, cancellationToken).ConfigureAwait(false);
        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    private SpeechRequest BuildRequest(string text)
    {
        var options = new SpeechOptions
        {
            Rate = _settingsService.Current.SpeechRate,
            VoiceName = _settingsService.Current.VoiceName
        };

        return new SpeechRequest(text, options);
    }

    private void NormalizeSavedVoiceSetting()
    {
        var normalized = NormalizeVoiceName(_settingsService.Current.VoiceName);
        if (string.Equals(_settingsService.Current.VoiceName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _settingsService.Current.VoiceName = normalized;
        _settingsService.Save();
    }

    private string? NormalizeVoiceName(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return null;
        }

        foreach (var availableVoice in _availableVoices)
        {
            if (string.Equals(availableVoice, voiceName, StringComparison.OrdinalIgnoreCase))
            {
                return availableVoice;
            }
        }

        return null;
    }

    private static bool ShouldRetryParagraphRetrieval(TextRetrievalResult retrieval)
    {
        var message = retrieval.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        return message.Contains("UI Automation", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("focused control", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("clipboard paragraph fallback", StringComparison.OrdinalIgnoreCase);
    }
}

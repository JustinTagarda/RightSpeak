using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class ReadingService : IReadingService
{
    private const int ChunkThresholdCharacters = 340;
    private const int ChunkTargetCharacters = 260;
    private const double ContinuationChunkPrimerSeconds = 0.08;
    private const string PiperEngineName = "Piper";
    private const string PreferredPiperLjspeechVoiceName = "piper:en_US-ljspeech-high";

    private readonly ISpeechService _speechService;
    private readonly ISelectedTextRetrievalService _selectedTextRetrievalService;
    private readonly IParagraphTextRetrievalService _paragraphTextRetrievalService;
    private readonly IDocumentTextRetrievalService _documentTextRetrievalService;
    private readonly IAppSettingsService _settingsService;
    private readonly IReadOnlyList<SpeechVoice> _availableVoices;

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
        _availableVoices = _speechService.GetInstalledVoices();
        NormalizeSavedVoiceSetting();
        EnsureDefaultVoiceSelectionForEmptyStorage();
    }

    public bool IsReading => _speechService.IsSpeaking;
    public IReadOnlyList<SpeechVoice> AvailableVoices => _availableVoices;
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

    public string TypedTextDraft
    {
        get => _settingsService.Current.TypedTextDraft ?? string.Empty;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_settingsService.Current.TypedTextDraft, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Current.TypedTextDraft = normalized;
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
            return SpeechResult.Failed(BuildSelectedTextFailureMessage());
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
            return SpeechResult.Failed(BuildParagraphFailureMessage());
        }

        var speechResult = await SpeakTextWithChunkingAsync(retrieval.Text, cancellationToken).ConfigureAwait(false);
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
            return SpeechResult.Failed(BuildDocumentFailureMessage());
        }

        var speechResult = await SpeakTextWithChunkingAsync(retrieval.Text, cancellationToken).ConfigureAwait(false);
        if (!speechResult.Success || speechResult.WasCancelled)
        {
            return speechResult;
        }

        return SpeechResult.Completed(retrieval.Message);
    }

    private SpeechRequest BuildRequest(string text)
    {
        return BuildRequest(text, leadingPrimerSecondsOverride: null);
    }

    private SpeechRequest BuildRequest(string text, double? leadingPrimerSecondsOverride)
    {
        var options = new SpeechOptions
        {
            Rate = _settingsService.Current.SpeechRate,
            VoiceName = _settingsService.Current.VoiceName,
            LeadingPrimerSecondsOverride = leadingPrimerSecondsOverride
        };

        return new SpeechRequest(text, options);
    }

    private async Task<SpeechResult> SpeakTextWithChunkingAsync(string text, CancellationToken cancellationToken)
    {
        var normalizedText = text?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            return SpeechResult.Failed("Nothing to read. Enter text first.");
        }

        if (normalizedText.Length < ChunkThresholdCharacters)
        {
            return await _speechService.SpeakAsync(BuildRequest(normalizedText), cancellationToken).ConfigureAwait(false);
        }

        var chunks = SplitIntoSpeechChunks(normalizedText, ChunkTargetCharacters);
        if (chunks.Count <= 1)
        {
            return await _speechService.SpeakAsync(BuildRequest(normalizedText), cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double? primerOverride = index == 0 ? null : ContinuationChunkPrimerSeconds;
            var result = await _speechService
                .SpeakAsync(BuildRequest(chunks[index], primerOverride), cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success || result.WasCancelled)
            {
                return result;
            }
        }

        return SpeechResult.Completed();
    }

    private static IReadOnlyList<string> SplitIntoSpeechChunks(string text, int targetChunkCharacters)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var token = new StringBuilder();
        var tokenEndsSentence = false;

        foreach (var ch in text)
        {
            token.Append(ch);
            if (ch == '.' || ch == '!' || ch == '?')
            {
                tokenEndsSentence = true;
            }

            if (!char.IsWhiteSpace(ch))
            {
                continue;
            }

            FlushToken();
        }

        FlushToken(force: true);
        FlushCurrent(force: true);

        return chunks;

        void FlushToken(bool force = false)
        {
            if (token.Length == 0)
            {
                return;
            }

            var tokenText = token.ToString();
            token.Clear();

            if (current.Length > 0 && current.Length + tokenText.Length > targetChunkCharacters && tokenEndsSentence)
            {
                FlushCurrent(force: true);
            }

            current.Append(tokenText);

            if (force || (tokenEndsSentence && current.Length >= targetChunkCharacters))
            {
                FlushCurrent(force: true);
            }

            tokenEndsSentence = false;
        }

        void FlushCurrent(bool force = false)
        {
            if (!force && current.Length < targetChunkCharacters)
            {
                return;
            }

            var chunk = current.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            current.Clear();
        }
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

    private void EnsureDefaultVoiceSelectionForEmptyStorage()
    {
        if (!string.IsNullOrWhiteSpace(_settingsService.Current.VoiceName))
        {
            return;
        }

        try
        {
            var selectedDefaultVoiceName = SelectDefaultVoiceForEmptyStorage();
            if (string.IsNullOrWhiteSpace(selectedDefaultVoiceName))
            {
                return;
            }

            _settingsService.Current.VoiceName = selectedDefaultVoiceName;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "voice_default_selection_failed",
                new Dictionary<string, string?>
                {
                    ["message"] = ex.Message
                });
            _settingsService.Current.VoiceName = null;
            _settingsService.Save();
        }
    }

    private string? SelectDefaultVoiceForEmptyStorage()
    {
        var hasAnyPiperVoice = _availableVoices.Any(voice =>
            string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal));
        if (hasAnyPiperVoice)
        {
            var piperLjspeechVoice = _availableVoices.FirstOrDefault(voice =>
                string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal) &&
                string.Equals(voice.Name, PreferredPiperLjspeechVoiceName, StringComparison.OrdinalIgnoreCase));
            if (piperLjspeechVoice is not null)
            {
                return piperLjspeechVoice.Name;
            }

            var piperLjspeechByFragment = _availableVoices.FirstOrDefault(voice =>
                string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal) &&
                voice.Name.Contains("ljspeech", StringComparison.OrdinalIgnoreCase));
            if (piperLjspeechByFragment is not null)
            {
                return piperLjspeechByFragment.Name;
            }

            return null;
        }

        var microsoftDavidVoice = _availableVoices
            .Where(voice =>
                !string.Equals(voice.Engine, PiperEngineName, StringComparison.Ordinal) &&
                (voice.Name.Contains("Microsoft David", StringComparison.OrdinalIgnoreCase) ||
                 voice.DisplayName.Contains("Microsoft David", StringComparison.OrdinalIgnoreCase) ||
                 voice.Name.Contains("David", StringComparison.OrdinalIgnoreCase) ||
                 voice.DisplayName.Contains("David", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(voice => voice.Engine, StringComparer.Ordinal)
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return microsoftDavidVoice?.Name;
    }

    private string? NormalizeVoiceName(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
        {
            return null;
        }

        foreach (var availableVoice in _availableVoices)
        {
            if (string.Equals(availableVoice.Name, voiceName, StringComparison.OrdinalIgnoreCase))
            {
                return availableVoice.Name;
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

    private static string BuildSelectedTextFailureMessage()
    {
        return "Couldn't read the selected text. Select the text in the other app, then try again.";
    }

    private static string BuildParagraphFailureMessage()
    {
        return "Couldn't read the current paragraph. Click in the paragraph you want, then try again.";
    }

    private static string BuildDocumentFailureMessage()
    {
        return "Couldn't read the document from that app.";
    }
}

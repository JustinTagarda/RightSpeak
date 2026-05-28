using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;
using RightSpeak.ViewModels;
using Xunit;

namespace RightSpeak.Tests;

public sealed class PremiumGatingPolicyTests
{
    [Fact]
    public async Task BasicMode_ReadDocument_RemainsAvailable()
    {
        var readingService = new FakeReadingService();
        var viewModel = CreateViewModel(readingService);
        viewModel.SetFocusedWindowContext("Notepad - test", hasExternalFocusedWindow: true);

        Assert.True(viewModel.ReadDocumentCommand.CanExecute(null));

        await ((AsyncCommand)viewModel.ReadDocumentCommand).ExecuteAsync();

        Assert.Equal(1, readingService.ReadDocumentCallCount);
    }

    [Fact]
    public async Task BasicMode_AllThemesRemainAvailable()
    {
        var appSettings = new FakeAppSettingsService();
        var viewModel = CreateViewModel(new FakeReadingService(), appSettings);

        Assert.Contains(AppThemes.Light, viewModel.ThemeOptions);
        Assert.Contains(AppThemes.Dark, viewModel.ThemeOptions);
        Assert.Contains(AppThemes.WindowsSettings, viewModel.ThemeOptions);

        viewModel.SelectedTheme = AppThemes.Dark;
        await Task.CompletedTask;

        Assert.Equal(AppThemes.Dark, appSettings.Current.Theme);
    }

    [Fact]
    public async Task BasicMode_FullStatusMessaging_RemainsAvailable()
    {
        var viewModel = CreateViewModel(new FakeReadingService());
        viewModel.SetFocusedWindowContext("Notepad - test", hasExternalFocusedWindow: true);

        await ((AsyncCommand)viewModel.ReadDocumentCommand).ExecuteAsync();

        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    [Fact]
    public void BasicMode_NonAllowedVoiceSelection_IsBlocked()
    {
        var readingService = new FakeReadingService();
        var viewModel = CreateViewModel(readingService);
        var blockedVoiceOption = "Microsoft Aria (OneCore)";

        viewModel.SelectedVoiceOption = blockedVoiceOption;

        Assert.Equal("System default", viewModel.SelectedVoiceOption);
        Assert.Null(readingService.SelectedVoiceName);
    }

    [Fact]
    public void BasicMode_HotkeyCustomization_IsBlocked()
    {
        var viewModel = CreateViewModel(new FakeReadingService());

        viewModel.ReadSelectedHotkeyKey = "A";

        Assert.Equal("S", viewModel.ReadSelectedHotkeyKey);
    }

    private static MainViewModel CreateViewModel(
        IReadingService readingService,
        IAppSettingsService? appSettingsService = null)
    {
        return new MainViewModel(
            readingService,
            new FakeHotkeySettingsService(),
            appSettingsService: appSettingsService ?? new FakeAppSettingsService(),
            premiumEntitlementService: new FakePremiumEntitlementService(
                new PremiumEntitlementState(false, true, false, "Basic mode is active.")),
            premiumPurchaseService: new FakePremiumPurchaseService(
                new PremiumPurchaseResult(PremiumPurchaseOutcome.NotSupported, "not supported")),
            storeNavigationService: new FakeStoreNavigationService());
    }

    private sealed class FakePremiumEntitlementService : IPremiumEntitlementService
    {
        private readonly PremiumEntitlementState _state;

        public FakePremiumEntitlementService(PremiumEntitlementState state)
        {
            _state = state;
        }

        public Task<PremiumEntitlementState> RefreshEntitlementAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_state);
        }
    }

    private sealed class FakePremiumPurchaseService : IPremiumPurchaseService
    {
        private readonly PremiumPurchaseResult _result;

        public FakePremiumPurchaseService(PremiumPurchaseResult result)
        {
            _result = result;
        }

        public Task<PremiumPurchaseResult> PurchasePremiumAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeStoreNavigationService : IStoreNavigationService
    {
        public bool OpenMainStorePage()
        {
            return true;
        }

        public bool OpenPremiumAddOnPage()
        {
            return true;
        }
    }

    private sealed class FakeAppSettingsService : IAppSettingsService
    {
        public AppSettings Current { get; } = new();

        public void Save()
        {
        }
    }

    private sealed class FakeHotkeySettingsService : IHotkeySettingsService
    {
        public IReadOnlyList<string> AvailableKeyOptions { get; } = new[] { "A", "S", "D", "X", "P" };
        public HotkeyModifierPreset ModifierPreset { get; set; } = HotkeyModifierPreset.AltShift;
        public string ReadSelectedKey { get; set; } = "S";
        public string ReadParagraphKey { get; set; } = "P";
        public string ReadDocumentKey { get; set; } = "D";
        public string StopKey { get; set; } = "X";

        public bool Save()
        {
            return true;
        }

        public HotkeyConfiguration BuildConfiguration()
        {
            return new HotkeyConfiguration(
                HotKeyModifiers.Alt | HotKeyModifiers.Shift,
                0x53,
                0x50,
                0x44,
                0x58);
        }
    }

    private sealed class FakeReadingService : IReadingService
    {
        public bool IsReading { get; set; }
        public bool IsPaused => false;
        public IReadOnlyList<SpeechVoice> AvailableVoices { get; } =
        [
            new SpeechVoice("ljspeech", "Ljspeech", "Piper"),
            new SpeechVoice("onecore-default", "Microsoft Aria", "OneCore")
        ];

        public int SpeechRate { get; set; }
        public string? SelectedVoiceName { get; set; }
        public string TypedTextDraft { get; set; } = string.Empty;
        public int ReadDocumentCallCount { get; private set; }

        public void RefreshAvailableVoices()
        {
        }

        public Task<SpeechResult> ReadTextAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SpeechResult.Completed("Read completed."));
        }

        public Task<SpeechResult> ReadSelectedTextAsync(CancellationToken cancellationToken = default, IProgress<ReadingProgressUpdate>? progress = null)
        {
            return Task.FromResult(SpeechResult.Completed("Selected read completed."));
        }

        public Task<SpeechResult> ReadParagraphAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SpeechResult.Completed("Paragraph read completed."));
        }

        public Task<SpeechResult> ReadDocumentAsync(CancellationToken cancellationToken = default, IProgress<ReadingProgressUpdate>? progress = null)
        {
            ReadDocumentCallCount++;
            progress?.Report(new ReadingProgressUpdate(ReadingProgressStage.Speaking, "Reading document..."));
            return Task.FromResult(SpeechResult.Completed("Document read completed."));
        }

        public Task<SpeechResult> PauseAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SpeechResult.Completed("Paused."));
        }

        public Task<SpeechResult> ResumeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SpeechResult.Completed("Resumed."));
        }

        public Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SpeechResult.Completed("Stopped."));
        }
    }
}

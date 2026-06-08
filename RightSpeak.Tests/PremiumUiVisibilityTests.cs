using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;
using RightSpeak.ViewModels;
using Xunit;

namespace RightSpeak.Tests;

public sealed class PremiumUiVisibilityTests
{
    [StaFact]
    public async Task DevelopmentMode_HidesFooterPremiumUi_AndDisablesUpsell()
    {
        var viewModel = CreateViewModel(
            new PremiumEntitlementState(
                true,
                false,
                false,
                "Development build: Premium gating is disabled.",
                ShouldShowPremiumUi: false));

        await viewModel.InitializePremiumStatusAsync();

        Assert.True(viewModel.IsPremiumOwned);
        Assert.False(viewModel.IsPremiumUiReady);
        Assert.False(viewModel.IsUpgradeButtonVisible);

        viewModel.ReadSelectedHotkeyKey = "A";

        Assert.Equal("A", viewModel.ReadSelectedHotkeyKey);
    }

    [StaFact]
    public async Task PackagedBasicMode_ShowsFooterPremiumUi()
    {
        var viewModel = CreateViewModel(
            new PremiumEntitlementState(
                false,
                true,
                false,
                "Basic mode is active."));

        await viewModel.InitializePremiumStatusAsync();

        Assert.False(viewModel.IsPremiumOwned);
        Assert.True(viewModel.IsPremiumUiReady);
        Assert.True(viewModel.IsUpgradeButtonVisible);
    }

    private static MainViewModel CreateViewModel(PremiumEntitlementState state)
    {
        return new MainViewModel(
            new FakeReadingService(),
            new FakeHotkeySettingsService(),
            appSettingsService: new FakeAppSettingsService(),
            premiumEntitlementService: new FakePremiumEntitlementService(state),
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
        public bool OpenMainStorePage() => true;
        public bool OpenPremiumAddOnPage() => true;
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
        public bool Save() => true;
        public HotkeyConfiguration BuildConfiguration() =>
            new(HotKeyModifiers.Alt | HotKeyModifiers.Shift, 0x53, 0x50, 0x44, 0x58);
    }

    private sealed class FakeReadingService : IReadingService
    {
        public bool IsReading => false;
        public bool IsPaused => false;
        public IReadOnlyList<SpeechVoice> AvailableVoices { get; } = new[] { new SpeechVoice("voice", "Voice", "Engine") };
        public int SpeechRate { get; set; }
        public string? SelectedVoiceName { get; set; }
        public string TypedTextDraft { get; set; } = string.Empty;
        public void RefreshAvailableVoices()
        {
        }

        public Task<SpeechResult> ReadTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(SpeechResult.Completed());
        public Task<SpeechResult> ReadSelectedTextAsync(CancellationToken cancellationToken = default, IProgress<ReadingProgressUpdate>? progress = null) => Task.FromResult(SpeechResult.Completed());
        public Task<SpeechResult> ReadParagraphAsync(CancellationToken cancellationToken = default) => Task.FromResult(SpeechResult.Completed());
        public Task<SpeechResult> ReadDocumentAsync(CancellationToken cancellationToken = default, IProgress<ReadingProgressUpdate>? progress = null) => Task.FromResult(SpeechResult.Completed());
        public Task<SpeechResult> PauseAsync(CancellationToken cancellationToken = default) => Task.FromResult(SpeechResult.Completed());
        public Task<SpeechResult> ResumeAsync(CancellationToken cancellationToken = default) => Task.FromResult(SpeechResult.Completed());
        public Task<SpeechResult> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(SpeechResult.Completed());
    }
}

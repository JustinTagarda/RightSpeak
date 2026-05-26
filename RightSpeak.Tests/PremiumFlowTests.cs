using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RightSpeak.Models;
using RightSpeak.Services;
using RightSpeak.ViewModels;
using RightSpeak.Interop;
using Xunit;

namespace RightSpeak.Tests;

public sealed class PremiumFlowTests
{
    [Fact]
    public async Task Upgrade_Succeeded_RefreshesEntitlement_AndUnlocksPremium()
    {
        var entitlement = new FakePremiumEntitlementService(
            new PremiumEntitlementState(true, true, false, "Premium is active."));
        var purchase = new FakePremiumPurchaseService(new PremiumPurchaseResult(PremiumPurchaseOutcome.Succeeded, "ok"));
        var navigation = new FakeStoreNavigationService();
        var viewModel = CreateViewModel(entitlement, purchase, navigation);

        await ((AsyncCommand)viewModel.UpgradeToPremiumCommand).ExecuteAsync();

        Assert.Equal(1, purchase.CallCount);
        Assert.Equal(1, entitlement.CallCount);
        Assert.True(viewModel.IsPremiumOwned);
        Assert.Equal("Premium", viewModel.AppModeText);
    }

    [Fact]
    public async Task Upgrade_AlreadyOwned_RefreshesEntitlement()
    {
        var entitlement = new FakePremiumEntitlementService(
            new PremiumEntitlementState(true, true, false, "Premium is active."));
        var purchase = new FakePremiumPurchaseService(new PremiumPurchaseResult(PremiumPurchaseOutcome.AlreadyOwned, "already"));
        var navigation = new FakeStoreNavigationService();
        var viewModel = CreateViewModel(entitlement, purchase, navigation);

        await ((AsyncCommand)viewModel.UpgradeToPremiumCommand).ExecuteAsync();

        Assert.Equal(1, purchase.CallCount);
        Assert.Equal(1, entitlement.CallCount);
        Assert.True(viewModel.IsPremiumOwned);
        Assert.False(viewModel.IsUpgradeAvailable);
    }

    [Fact]
    public async Task Upgrade_NotSupported_UsesStoreNavigationFallback()
    {
        var entitlement = new FakePremiumEntitlementService(
            new PremiumEntitlementState(false, true, false, "Basic mode is active."));
        var purchase = new FakePremiumPurchaseService(new PremiumPurchaseResult(PremiumPurchaseOutcome.NotSupported, "unsupported"));
        var navigation = new FakeStoreNavigationService();
        var viewModel = CreateViewModel(entitlement, purchase, navigation);

        await ((AsyncCommand)viewModel.UpgradeToPremiumCommand).ExecuteAsync();

        Assert.Equal(1, purchase.CallCount);
        Assert.Equal(0, entitlement.CallCount);
        Assert.Equal(1, navigation.CallCount);
        Assert.False(viewModel.IsPremiumOwned);
    }

    [Fact]
    public async Task InitializePremiumStatus_UsesEntitlementState()
    {
        var entitlement = new FakePremiumEntitlementService(
            new PremiumEntitlementState(true, true, false, "Premium is active."));
        var purchase = new FakePremiumPurchaseService(new PremiumPurchaseResult(PremiumPurchaseOutcome.Canceled, "canceled"));
        var navigation = new FakeStoreNavigationService();
        var viewModel = CreateViewModel(entitlement, purchase, navigation);

        await viewModel.InitializePremiumStatusAsync();

        Assert.True(viewModel.IsPremiumOwned);
        Assert.Equal("Premium", viewModel.AppModeText);
        Assert.False(viewModel.IsUpgradeAvailable);
    }

    private static MainViewModel CreateViewModel(
        IPremiumEntitlementService entitlementService,
        IPremiumPurchaseService purchaseService,
        IStoreNavigationService navigationService)
    {
        return new MainViewModel(
            new FakeReadingService(),
            new FakeHotkeySettingsService(),
            appSettingsService: new FakeAppSettingsService(),
            premiumEntitlementService: entitlementService,
            premiumPurchaseService: purchaseService,
            storeNavigationService: navigationService);
    }

    private sealed class FakePremiumEntitlementService : IPremiumEntitlementService
    {
        private readonly Queue<PremiumEntitlementState> _states;
        public int CallCount { get; private set; }

        public FakePremiumEntitlementService(params PremiumEntitlementState[] states)
        {
            _states = new Queue<PremiumEntitlementState>(states);
        }

        public Task<PremiumEntitlementState> RefreshEntitlementAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_states.Count == 0)
            {
                return Task.FromResult(new PremiumEntitlementState(false, true, false, "Basic mode is active."));
            }

            return Task.FromResult(_states.Dequeue());
        }
    }

    private sealed class FakePremiumPurchaseService : IPremiumPurchaseService
    {
        private readonly PremiumPurchaseResult _result;
        public int CallCount { get; private set; }

        public FakePremiumPurchaseService(PremiumPurchaseResult result)
        {
            _result = result;
        }

        public Task<PremiumPurchaseResult> PurchasePremiumAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeStoreNavigationService : IStoreNavigationService
    {
        public int CallCount { get; private set; }

        public bool OpenMainStorePage()
        {
            CallCount++;
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

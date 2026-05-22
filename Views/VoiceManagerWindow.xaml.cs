using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using RightSpeak.Services;
using RightSpeak.ViewModels;

namespace RightSpeak.Views;

public partial class VoiceManagerWindow : Window
{
    private static readonly TimeSpan CloseCancellationWaitTimeout = TimeSpan.FromSeconds(5);
    private readonly VoiceManagerViewModel _viewModel;
    private bool _closeAfterCancellation;
    private bool _cancelAndCloseInProgress;

    public VoiceManagerWindow(VoiceManagerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await _viewModel.LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Info("voice_manager_load_canceled");
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "voice_manager_load_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        DragMove();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _ = sender;

        if (_closeAfterCancellation || _viewModel.IsOperationIdle)
        {
            return;
        }

        e.Cancel = true;
        if (_cancelAndCloseInProgress)
        {
            return;
        }

        _cancelAndCloseInProgress = true;
        try
        {
            _viewModel.CancelActiveOperations();
            await WaitForOperationIdleAsync().ConfigureAwait(true);
            _closeAfterCancellation = true;
            Close();
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "voice_manager_close_after_cancel_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            _closeAfterCancellation = true;
            Close();
        }
        finally
        {
            _cancelAndCloseInProgress = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private async Task WaitForOperationIdleAsync()
    {
        var deadline = DateTime.UtcNow + CloseCancellationWaitTimeout;
        while (!_viewModel.IsOperationIdle && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50).ConfigureAwait(true);
        }

        if (!_viewModel.IsOperationIdle)
        {
            AppDiagnostics.Warn("voice_manager_close_cancel_wait_timeout");
        }
    }

}

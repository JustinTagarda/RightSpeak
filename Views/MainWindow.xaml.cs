using System.Windows;
using System.Windows.Interop;
using RightSpeak.Services;
using RightSpeak.ViewModels;

namespace RightSpeak.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly uint _activateWindowMessageId;
    private HwndSource? _windowSource;

    public MainWindow(MainViewModel viewModel, IGlobalHotkeyService hotkeyService, uint activateWindowMessageId)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _hotkeyService = hotkeyService;
        _activateWindowMessageId = activateWindowMessageId;
        DataContext = _viewModel;

        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, System.EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WndProc);

        var registered = _hotkeyService.RegisterHotkeys(handle);
        _hotkeyService.ReadSelectedHotkeyPressed += OnReadSelectedHotkeyPressed;
        _hotkeyService.ReadTypedTextHotkeyPressed += OnReadTypedTextHotkeyPressed;
        _hotkeyService.StopHotkeyPressed += OnStopHotkeyPressed;

        if (!registered)
        {
            _viewModel.SetStatusMessage(_hotkeyService.LastRegistrationStatus);
        }
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        _hotkeyService.ReadSelectedHotkeyPressed -= OnReadSelectedHotkeyPressed;
        _hotkeyService.ReadTypedTextHotkeyPressed -= OnReadTypedTextHotkeyPressed;
        _hotkeyService.StopHotkeyPressed -= OnStopHotkeyPressed;
        _hotkeyService.Dispose();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WndProc);
            _windowSource = null;
        }
    }

    private void OnReadSelectedHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (_viewModel.ReadSelectedTextCommand.CanExecute(null))
        {
            _viewModel.ReadSelectedTextCommand.Execute(null);
        }
    }

    private void OnReadTypedTextHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (_viewModel.ReadCommand.CanExecute(null))
        {
            _viewModel.ReadCommand.Execute(null);
        }
    }

    private void OnStopHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (_viewModel.StopCommand.CanExecute(null))
        {
            _viewModel.StopCommand.Execute(null);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg != _activateWindowMessageId)
        {
            return nint.Zero;
        }

        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
        handled = true;
        return nint.Zero;
    }
}

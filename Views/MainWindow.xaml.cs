using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using RightSpeak.Services;
using RightSpeak.ViewModels;

namespace RightSpeak.Views;

public partial class MainWindow : Window
{
    private static readonly string AppVersionTextValue = BuildVersionText();
    private readonly MainViewModel _viewModel;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly Func<Action, Task>? _executeFocusSensitiveReadAsync;
    private readonly uint _activateWindowMessageId;
    private readonly bool _placeOnStartup;
    private bool _hasPlacedOnStartup;
    private HwndSource? _windowSource;

    public MainWindow(
        MainViewModel viewModel,
        IGlobalHotkeyService hotkeyService,
        uint activateWindowMessageId,
        Func<Action, Task>? executeFocusSensitiveReadAsync = null,
        bool placeOnStartup = false)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _hotkeyService = hotkeyService;
        _executeFocusSensitiveReadAsync = executeFocusSensitiveReadAsync;
        _activateWindowMessageId = activateWindowMessageId;
        _placeOnStartup = placeOnStartup;
        DataContext = _viewModel;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public string AppVersionText => AppVersionTextValue;

    private void OnSourceInitialized(object? sender, System.EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WndProc);

        var registered = _hotkeyService.RegisterHotkeys(handle);
        _hotkeyService.ReadSelectedHotkeyPressed += OnReadSelectedHotkeyPressed;
        _hotkeyService.ReadParagraphHotkeyPressed += OnReadParagraphHotkeyPressed;
        _hotkeyService.ReadDocumentHotkeyPressed += OnReadDocumentHotkeyPressed;
        _hotkeyService.StopHotkeyPressed += OnStopHotkeyPressed;

        if (!registered)
        {
            _viewModel.SetStatusMessage(_hotkeyService.LastRegistrationStatus);
        }
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        Loaded -= OnLoaded;
        _hotkeyService.ReadSelectedHotkeyPressed -= OnReadSelectedHotkeyPressed;
        _hotkeyService.ReadParagraphHotkeyPressed -= OnReadParagraphHotkeyPressed;
        _hotkeyService.ReadDocumentHotkeyPressed -= OnReadDocumentHotkeyPressed;
        _hotkeyService.StopHotkeyPressed -= OnStopHotkeyPressed;
        _hotkeyService.Dispose();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WndProc);
            _windowSource = null;
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (!_placeOnStartup || _hasPlacedOnStartup)
        {
            return;
        }

        PositionBottomRightOnPrimaryWorkingArea();
        _hasPlacedOnStartup = true;
    }

    private void OnReadSelectedHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (_viewModel.ReadSelectedTextCommand.CanExecute(null))
        {
            _viewModel.ReadSelectedTextCommand.Execute(null);
        }
    }

    private void OnStopHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (_viewModel.StopCommand.CanExecute(null))
        {
            _viewModel.StopCommand.Execute(null);
        }
    }

    private async void OnReadParagraphHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(ExecuteReadParagraph).ConfigureAwait(true);
            return;
        }

        ExecuteReadParagraph();
    }

    private async void OnReadDocumentHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(ExecuteReadDocument).ConfigureAwait(true);
            return;
        }

        ExecuteReadDocument();
    }

    private async void OnReadParagraphButtonClick(object sender, RoutedEventArgs e)
    {
        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(ExecuteReadParagraph).ConfigureAwait(true);
            return;
        }

        ExecuteReadParagraph();
    }

    private async void OnReadSelectedTextButtonClick(object sender, RoutedEventArgs e)
    {
        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(ExecuteReadSelectedText).ConfigureAwait(true);
            return;
        }

        ExecuteReadSelectedText();
    }

    private async void OnReadDocumentButtonClick(object sender, RoutedEventArgs e)
    {
        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(ExecuteReadDocument).ConfigureAwait(true);
            return;
        }

        ExecuteReadDocument();
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

    private void ExecuteReadParagraph()
    {
        if (_viewModel.ReadParagraphCommand.CanExecute(null))
        {
            _viewModel.ReadParagraphCommand.Execute(null);
        }
    }

    private void ExecuteReadSelectedText()
    {
        if (_viewModel.ReadSelectedTextCommand.CanExecute(null))
        {
            _viewModel.ReadSelectedTextCommand.Execute(null);
        }
    }

    private void ExecuteReadDocument()
    {
        if (_viewModel.ReadDocumentCommand.CanExecute(null))
        {
            _viewModel.ReadDocumentCommand.Execute(null);
        }
    }

    private void PositionBottomRightOnPrimaryWorkingArea()
    {
        var primaryScreen = Screen.PrimaryScreen;
        if (primaryScreen is null)
        {
            return;
        }

        var workingAreaPx = primaryScreen.WorkingArea;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var transformFromDevice = source.CompositionTarget.TransformFromDevice;
        var topLeft = transformFromDevice.Transform(new System.Windows.Point(workingAreaPx.Left, workingAreaPx.Top));
        var bottomRight = transformFromDevice.Transform(new System.Windows.Point(workingAreaPx.Right, workingAreaPx.Bottom));

        const double edgePaddingDip = 12d;
        var windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        var windowHeight = ActualHeight > 0 ? ActualHeight : Height;

        Left = bottomRight.X - windowWidth - edgePaddingDip;
        Top = bottomRight.Y - windowHeight - edgePaddingDip;

        if (Left < topLeft.X + edgePaddingDip)
        {
            Left = topLeft.X + edgePaddingDip;
        }

        if (Top < topLeft.Y + edgePaddingDip)
        {
            Top = topLeft.Y + edgePaddingDip;
        }
    }

    private static string BuildVersionText()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        if (version is null)
        {
            return "Version: 0.0.0";
        }

        return $"Version: {version.Major}.{version.Minor}.{version.Build}";
    }
}

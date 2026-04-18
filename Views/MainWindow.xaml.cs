using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Input;
using Rect = System.Windows.Rect;
using RightSpeak.Services;
using RightSpeak.ViewModels;

namespace RightSpeak.Views;

public partial class MainWindow : Window
{
    private static readonly string AppVersionTextValue = BuildVersionText();
    private readonly MainViewModel _viewModel;
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly Func<string, string, Func<Task>, Task>? _executeFocusSensitiveReadAsync;
    private readonly uint _activateWindowMessageId;
    private readonly bool _placeOnStartup;
    private bool _hasPlacedOnStartup;
    private HwndSource? _windowSource;

    public MainWindow(
        MainViewModel viewModel,
        IGlobalHotkeyService hotkeyService,
        uint activateWindowMessageId,
        Func<string, string, Func<Task>, Task>? executeFocusSensitiveReadAsync = null,
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

        PositionBottomRightOnActiveWorkingArea();
        EnsureVisibleOnScreen();
        _hasPlacedOnStartup = true;
    }

    public void EnsureVisibleOnScreen()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var transformFromDevice = source.CompositionTarget.TransformFromDevice;
        var windowRect = new Rect(
            Left,
            Top,
            ActualWidth > 0 ? ActualWidth : Width,
            ActualHeight > 0 ? ActualHeight : Height);

        var hasVisibleIntersection = Screen.AllScreens.Any(screen =>
        {
            var area = screen.WorkingArea;
            var topLeft = transformFromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
            var bottomRight = transformFromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
            var screenRect = new Rect(topLeft, bottomRight);
            var intersection = Rect.Intersect(windowRect, screenRect);
            return !intersection.IsEmpty && intersection.Width >= 120 && intersection.Height >= 120;
        });

        if (hasVisibleIntersection)
        {
            return;
        }

        var primary = Screen.PrimaryScreen;
        if (primary is null)
        {
            return;
        }

        var primaryTopLeft = transformFromDevice.Transform(new System.Windows.Point(primary.WorkingArea.Left, primary.WorkingArea.Top));
        var primaryBottomRight = transformFromDevice.Transform(new System.Windows.Point(primary.WorkingArea.Right, primary.WorkingArea.Bottom));
        var primaryRect = new Rect(primaryTopLeft, primaryBottomRight);

        var width = windowRect.Width > 0 ? windowRect.Width : 700;
        var height = windowRect.Height > 0 ? windowRect.Height : 700;
        Left = primaryRect.Left + Math.Max(0, (primaryRect.Width - width) / 2);
        Top = primaryRect.Top + Math.Max(0, (primaryRect.Height - height) / 2);
    }

    private async void OnReadSelectedHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (!_viewModel.ReadSelectedTextCommand.CanExecute(null))
        {
            return;
        }

        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(
                "read_selected_text_external",
                "hotkey",
                ExecuteReadSelectedTextAsync).ConfigureAwait(true);
            return;
        }

        await ExecuteReadSelectedTextAsync().ConfigureAwait(true);
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
        if (!_viewModel.ReadParagraphCommand.CanExecute(null))
        {
            return;
        }

        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(
                "read_paragraph_external",
                "hotkey",
                ExecuteReadParagraphAsync).ConfigureAwait(true);
            return;
        }

        await ExecuteReadParagraphAsync().ConfigureAwait(true);
    }

    private async void OnReadDocumentHotkeyPressed(object? sender, System.EventArgs e)
    {
        if (!_viewModel.ReadDocumentCommand.CanExecute(null))
        {
            return;
        }

        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(
                "read_document_external",
                "hotkey",
                ExecuteReadDocumentAsync).ConfigureAwait(true);
            return;
        }

        await ExecuteReadDocumentAsync().ConfigureAwait(true);
    }

    private async void OnReadParagraphButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ReadParagraphCommand.CanExecute(null))
        {
            return;
        }

        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(
                "read_paragraph_external",
                "main_window_button",
                ExecuteReadParagraphAsync).ConfigureAwait(true);
            return;
        }

        await ExecuteReadParagraphAsync().ConfigureAwait(true);
    }

    private async void OnReadSelectedTextButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ReadSelectedTextCommand.CanExecute(null))
        {
            return;
        }

        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(
                "read_selected_text_external",
                "main_window_button",
                ExecuteReadSelectedTextAsync).ConfigureAwait(true);
            return;
        }

        await ExecuteReadSelectedTextAsync().ConfigureAwait(true);
    }

    private async void OnReadDocumentButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.ReadDocumentCommand.CanExecute(null))
        {
            return;
        }

        if (_executeFocusSensitiveReadAsync is not null)
        {
            await _executeFocusSensitiveReadAsync(
                "read_document_external",
                "main_window_button",
                ExecuteReadDocumentAsync).ConfigureAwait(true);
            return;
        }

        await ExecuteReadDocumentAsync().ConfigureAwait(true);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg != _activateWindowMessageId)
        {
            return nint.Zero;
        }

        RevealWindow();
        handled = true;
        return nint.Zero;
    }

    public void RevealWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        EnsureVisibleOnScreen();

        // Force z-order activation even when other apps are currently foreground.
        Topmost = true;
        Activate();
        Focus();
        Topmost = false;
    }

    private Task ExecuteReadParagraphAsync()
    {
        if (_viewModel.ReadParagraphCommand.CanExecute(null))
        {
            return ExecuteCommandAsync(_viewModel.ReadParagraphCommand);
        }

        return Task.CompletedTask;
    }

    private Task ExecuteReadSelectedTextAsync()
    {
        if (_viewModel.ReadSelectedTextCommand.CanExecute(null))
        {
            return ExecuteCommandAsync(_viewModel.ReadSelectedTextCommand);
        }

        return Task.CompletedTask;
    }

    private Task ExecuteReadDocumentAsync()
    {
        if (_viewModel.ReadDocumentCommand.CanExecute(null))
        {
            return ExecuteCommandAsync(_viewModel.ReadDocumentCommand);
        }

        return Task.CompletedTask;
    }

    private static Task ExecuteCommandAsync(ICommand command)
    {
        if (command is AsyncCommand asyncCommand)
        {
            return asyncCommand.ExecuteAsync();
        }

        command.Execute(null);
        return Task.CompletedTask;
    }

    private void PositionBottomRightOnActiveWorkingArea()
    {
        var mousePosition = Control.MousePosition;
        var targetScreen = Screen.FromPoint(mousePosition) ?? Screen.PrimaryScreen;
        if (targetScreen is null)
        {
            return;
        }

        var workingAreaPx = targetScreen.WorkingArea;
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

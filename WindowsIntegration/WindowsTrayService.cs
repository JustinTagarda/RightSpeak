using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using RightSpeak.Interop;
using RightSpeak.Services;
using System.Windows.Forms;

namespace RightSpeak.WindowsIntegration;

public sealed class WindowsTrayService : ITrayService
{
    private static readonly string[] IgnoredForegroundWindowClasses =
    {
        "Shell_TrayWnd",
        "TrayNotifyWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland"
    };

    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private System.Windows.Forms.Timer? _foregroundWindowTrackerTimer;
    private ToolStripMenuItem? _readTypedTextMenuItem;
    private ToolStripMenuItem? _readSelectedMenuItem;
    private ToolStripMenuItem? _stopMenuItem;
    private WindowFocusInterop.WinEventProc? _foregroundChangedCallback;
    private nint _foregroundChangedHook;
    private nint _lastExternalForegroundWindow;
    private bool _isContextMenuOpen;
    private bool _disposed;

    public event EventHandler? ReadTypedTextRequested;
    public event EventHandler? ReadSelectedRequested;
    public event EventHandler? ReadParagraphRequested;
    public event EventHandler? ReadDocumentRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;

    public void Initialize()
    {
        ThrowIfDisposed();

        if (_notifyIcon is not null)
        {
            return;
        }

        var menu = new ContextMenuStrip();
        menu.Opening += OnMenuOpening;
        menu.Closed += OnMenuClosed;
        _readTypedTextMenuItem = new ToolStripMenuItem("Read Typed Text", null, (_, _) => QueueMenuAction(menu, () => ReadTypedTextRequested?.Invoke(this, EventArgs.Empty)));
        _readSelectedMenuItem = new ToolStripMenuItem("Read Selected Text", null, (_, _) => QueueMenuAction(menu, () => ReadSelectedRequested?.Invoke(this, EventArgs.Empty)));
        _stopMenuItem = new ToolStripMenuItem("Stop Reading", null, (_, _) => QueueMenuAction(menu, () => StopRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(_readTypedTextMenuItem);
        menu.Items.Add(_readSelectedMenuItem);
        menu.Items.Add("Read Paragraph", null, (_, _) => QueueMenuAction(menu, () => ReadParagraphRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add("Read Document", null, (_, _) => QueueMenuAction(menu, () => ReadDocumentRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(_stopMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show RightSpeak", null, (_, _) => QueueMenuAction(menu, () => ShowRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add("Exit", null, (_, _) => QueueMenuAction(menu, () => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "RightSpeak",
            Visible = true,
            ContextMenuStrip = menu
        };
        _contextMenu = menu;

        _foregroundChangedCallback = OnForegroundWindowChanged;
        _foregroundChangedHook = WindowFocusInterop.SetForegroundChangedHook(_foregroundChangedCallback);

        _foregroundWindowTrackerTimer = new System.Windows.Forms.Timer
        {
            Interval = 250
        };
        _foregroundWindowTrackerTimer.Tick += OnForegroundWindowTrackerTick;
        _foregroundWindowTrackerTimer.Start();

        _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
        _notifyIcon.MouseClick += OnNotifyIconMouseClick;
    }

    public void UpdateHotkeyHints(string readSelectedKey, string readTypedTextKey, string stopKey)
    {
        if (_readSelectedMenuItem is not null)
        {
            _readSelectedMenuItem.Text = $"Read Selected Text (Ctrl+Shift+{NormalizeKey(readSelectedKey)})";
        }

        if (_readTypedTextMenuItem is not null)
        {
            _readTypedTextMenuItem.Text = $"Read Typed Text (Ctrl+Shift+{NormalizeKey(readTypedTextKey)})";
        }

        if (_stopMenuItem is not null)
        {
            _stopMenuItem.Text = $"Stop Reading (Ctrl+Shift+{NormalizeKey(stopKey)})";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
            _notifyIcon.MouseClick -= OnNotifyIconMouseClick;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        if (_contextMenu is not null)
        {
            _contextMenu.Opening -= OnMenuOpening;
            _contextMenu.Closed -= OnMenuClosed;
            _contextMenu.Dispose();
            _contextMenu = null;
        }

        if (_foregroundWindowTrackerTimer is not null)
        {
            _foregroundWindowTrackerTimer.Stop();
            _foregroundWindowTrackerTimer.Tick -= OnForegroundWindowTrackerTick;
            _foregroundWindowTrackerTimer.Dispose();
            _foregroundWindowTrackerTimer = null;
        }

        if (_foregroundChangedHook != nint.Zero)
        {
            WindowFocusInterop.UnsetForegroundChangedHook(_foregroundChangedHook);
            _foregroundChangedHook = nint.Zero;
        }
        _foregroundChangedCallback = null;

        _disposed = true;
    }

    public bool TryRestoreLastExternalForegroundWindow()
    {
        if (_lastExternalForegroundWindow == nint.Zero)
        {
            AppDiagnostics.Warn("tray_restore_skipped_no_target");
            return false;
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            WindowFocusInterop.TryActivateWindow(_lastExternalForegroundWindow);
            Thread.Sleep(80);

            var currentForeground = WindowFocusInterop.GetForegroundWindow();
            if (currentForeground == _lastExternalForegroundWindow)
            {
                Thread.Sleep(120);
                return true;
            }
        }

        var finalForeground = WindowFocusInterop.GetForegroundWindow();
        var failureData = BuildWindowDiagnostics("target", _lastExternalForegroundWindow);
        foreach (var pair in BuildWindowDiagnostics("actual", finalForeground))
        {
            failureData[pair.Key] = pair.Value;
        }

        AppDiagnostics.Warn("tray_restore_failed", failureData);
        return finalForeground == _lastExternalForegroundWindow;
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isContextMenuOpen = true;
        CaptureExternalForegroundWindow();
    }

    private void OnNotifyIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Left)
        {
            CaptureExternalForegroundWindow();
        }
    }

    private void OnForegroundWindowTrackerTick(object? sender, EventArgs e)
    {
        if (_isContextMenuOpen)
        {
            return;
        }

        CaptureExternalForegroundWindow();
    }

    private void CaptureExternalForegroundWindow()
    {
        CaptureExternalForegroundWindow(WindowFocusInterop.GetForegroundWindow());
    }

    private void CaptureExternalForegroundWindow(nint foregroundWindow)
    {
        if (foregroundWindow == nint.Zero)
        {
            return;
        }

        WindowFocusInterop.GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return;
        }

        if (IsIgnoredForegroundWindowClass(foregroundWindow))
        {
            return;
        }

        _lastExternalForegroundWindow = foregroundWindow;
    }

    private void OnForegroundWindowChanged(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || _isContextMenuOpen)
        {
            return;
        }

        CaptureExternalForegroundWindow(hwnd);
    }

    private void OnMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        _isContextMenuOpen = false;
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    private static void QueueMenuAction(ContextMenuStrip menu, Action action)
    {
        menu.Close();
        menu.BeginInvoke(action);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsTrayService));
        }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/Icons/RightSpeak.ico"));
            if (resource is null)
            {
                return SystemIcons.Application;
            }

            using (resource.Stream)
            {
                using var icon = new Icon(resource.Stream);
                return (Icon)icon.Clone();
            }
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "?";
        }

        var normalized = key.Trim().ToUpperInvariant();
        return normalized.Length == 1 ? normalized : "?";
    }

    private static bool IsIgnoredForegroundWindowClass(nint windowHandle)
    {
        var className = WindowFocusInterop.GetWindowClassName(windowHandle);
        if (string.IsNullOrWhiteSpace(className))
        {
            return false;
        }

        return IgnoredForegroundWindowClasses.Any(ignored =>
            string.Equals(className, ignored, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string?> BuildWindowDiagnostics(string prefix, nint windowHandle)
    {
        var data = new Dictionary<string, string?>
        {
            [$"{prefix}Hwnd"] = $"0x{windowHandle.ToInt64():X}",
            [$"{prefix}Class"] = WindowFocusInterop.GetWindowClassName(windowHandle),
            [$"{prefix}Title"] = WindowFocusInterop.GetWindowText(windowHandle)
        };

        WindowFocusInterop.GetWindowThreadProcessId(windowHandle, out var processId);
        data[$"{prefix}ProcessId"] = processId.ToString();
        data[$"{prefix}ProcessName"] = TryGetProcessName(processId);
        return data;
    }

    private static string? TryGetProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}

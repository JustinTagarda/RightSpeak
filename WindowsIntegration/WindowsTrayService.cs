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
    private const int FocusRestoreTimeoutMilliseconds = 450;
    private const int FocusRestorePollMilliseconds = 25;
    private const int FocusRestoreReactivationIntervalMilliseconds = 80;
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
    private ToolStripMenuItem? _readSelectedMenuItem;
    private ToolStripMenuItem? _readParagraphMenuItem;
    private ToolStripMenuItem? _readDocumentMenuItem;
    private ToolStripMenuItem? _stopMenuItem;
    private WindowFocusInterop.WinEventProc? _foregroundChangedCallback;
    private nint _foregroundChangedHook;
    private nint _lastExternalForegroundWindow;
    private nint _lastObservedForegroundWindow;
    private string _currentForegroundWindowTitle = "Current app";
    private bool _hasExternalForegroundWindow;
    private bool _isContextMenuOpen;
    private bool _disposed;

    public event EventHandler? ReadSelectedRequested;
    public event EventHandler? ReadParagraphRequested;
    public event EventHandler? ReadDocumentRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? ShowRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? ForegroundWindowChanged;

    public string CurrentForegroundWindowTitle => _currentForegroundWindowTitle;
    public bool HasExternalForegroundWindow => _hasExternalForegroundWindow;

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
        _readSelectedMenuItem = new ToolStripMenuItem("Read Selected Text", null, (_, _) => QueueMenuAction(menu, () => ReadSelectedRequested?.Invoke(this, EventArgs.Empty)));
        _readParagraphMenuItem = new ToolStripMenuItem("Read Paragraph", null, (_, _) => QueueMenuAction(menu, () => ReadParagraphRequested?.Invoke(this, EventArgs.Empty)));
        _readDocumentMenuItem = new ToolStripMenuItem("Read Document", null, (_, _) => QueueMenuAction(menu, () => ReadDocumentRequested?.Invoke(this, EventArgs.Empty)));
        _stopMenuItem = new ToolStripMenuItem("Stop Reading", null, (_, _) => QueueMenuAction(menu, () => StopRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(_readSelectedMenuItem);
        menu.Items.Add(_readParagraphMenuItem);
        menu.Items.Add(_readDocumentMenuItem);
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

    public void UpdateHotkeyHints(string modifierLabel, string readSelectedKey, string readParagraphKey, string readDocumentKey, string stopKey)
    {
        var normalizedModifier = NormalizeModifierLabel(modifierLabel);
        if (_readSelectedMenuItem is not null)
        {
            _readSelectedMenuItem.Text = $"Read Selected Text ({normalizedModifier}+{NormalizeKey(readSelectedKey)})";
        }

        if (_readParagraphMenuItem is not null)
        {
            _readParagraphMenuItem.Text = $"Read Paragraph ({normalizedModifier}+{NormalizeKey(readParagraphKey)})";
        }

        if (_readDocumentMenuItem is not null)
        {
            _readDocumentMenuItem.Text = $"Read Document ({normalizedModifier}+{NormalizeKey(readDocumentKey)})";
        }

        if (_stopMenuItem is not null)
        {
            _stopMenuItem.Text = $"Stop Reading ({normalizedModifier}+{NormalizeKey(stopKey)})";
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

        if (!TryValidateRememberedExternalWindow(_lastExternalForegroundWindow, out var validationReason))
        {
            ClearRememberedExternalTarget(validationReason ?? "validation_failed_before_restore");
            AppDiagnostics.Warn("tray_restore_skipped_invalid_target");
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var activationAttempts = 0;
        var deadlineUtc = DateTime.UtcNow.AddMilliseconds(FocusRestoreTimeoutMilliseconds);
        var nextActivationUtc = DateTime.UtcNow;
        while (DateTime.UtcNow < deadlineUtc)
        {
            var currentForeground = WindowFocusInterop.GetForegroundWindow();
            if (currentForeground == _lastExternalForegroundWindow)
            {
                stopwatch.Stop();
                var successData = BuildWindowDiagnostics("target", _lastExternalForegroundWindow);
                successData["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString();
                successData["activationAttempts"] = activationAttempts.ToString();
                AppDiagnostics.Info("tray_restore_succeeded", successData);
                return true;
            }

            var nowUtc = DateTime.UtcNow;
            if (nowUtc >= nextActivationUtc)
            {
                WindowFocusInterop.TryActivateWindow(_lastExternalForegroundWindow);
                activationAttempts++;
                nextActivationUtc = nowUtc.AddMilliseconds(FocusRestoreReactivationIntervalMilliseconds);
            }

            Thread.Sleep(FocusRestorePollMilliseconds);
        }

        var finalForeground = WindowFocusInterop.GetForegroundWindow();
        var failureData = BuildWindowDiagnostics("target", _lastExternalForegroundWindow);
        foreach (var pair in BuildWindowDiagnostics("actual", finalForeground))
        {
            failureData[pair.Key] = pair.Value;
        }

        stopwatch.Stop();
        failureData["elapsedMs"] = stopwatch.ElapsedMilliseconds.ToString();
        failureData["activationAttempts"] = activationAttempts.ToString();
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
        var shouldLogProbe = foregroundWindow != _lastObservedForegroundWindow;
        _lastObservedForegroundWindow = foregroundWindow;

        if (foregroundWindow == nint.Zero)
        {
            if (shouldLogProbe)
            {
                AppDiagnostics.Info(
                    "foreground_probe",
                    new Dictionary<string, string?>
                    {
                        ["classification"] = "none",
                        ["reason"] = "foreground_window_zero",
                        ["hwnd"] = "0x0"
                    });
            }
            return;
        }

        WindowFocusInterop.GetWindowThreadProcessId(foregroundWindow, out var processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            if (shouldLogProbe)
            {
                var probeData = BuildWindowDiagnostics("window", foregroundWindow);
                probeData["classification"] = "self_or_invalid";
                probeData["reason"] = processId == 0 ? "process_id_zero" : "same_process";
                AppDiagnostics.Info("foreground_probe", probeData);
            }
            return;
        }

        if (IsIgnoredForegroundWindowClass(foregroundWindow))
        {
            if (shouldLogProbe)
            {
                var probeData = BuildWindowDiagnostics("window", foregroundWindow);
                probeData["classification"] = "ignored_shell_window";
                probeData["reason"] = "ignored_foreground_class";
                AppDiagnostics.Info("foreground_probe", probeData);
            }
            return;
        }

        var title = WindowFocusInterop.GetWindowText(foregroundWindow);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Current app";
        }

        if (shouldLogProbe)
        {
            var probeData = BuildWindowDiagnostics("window", foregroundWindow);
            probeData["classification"] = "external";
            probeData["reason"] = "accepted_external_window";
            AppDiagnostics.Info("foreground_probe", probeData);
        }

        SetForegroundWindowState(title, hasExternalForegroundWindow: true);
        _lastExternalForegroundWindow = foregroundWindow;
    }

    private bool TryValidateRememberedExternalWindow(nint windowHandle, out string? reason)
    {
        reason = null;
        if (windowHandle == nint.Zero)
        {
            reason = "target_hwnd_zero";
            return false;
        }

        WindowFocusInterop.GetWindowThreadProcessId(windowHandle, out var processId);
        if (processId == 0)
        {
            reason = "target_process_id_zero";
            return false;
        }

        if (processId == Environment.ProcessId)
        {
            reason = "target_same_process";
            return false;
        }

        if (IsIgnoredForegroundWindowClass(windowHandle))
        {
            reason = "target_ignored_window_class";
            return false;
        }

        return true;
    }

    private void ClearRememberedExternalTarget(string reason)
    {
        var previousTarget = _lastExternalForegroundWindow;
        if (previousTarget != nint.Zero)
        {
            var validationData = BuildWindowDiagnostics("target", previousTarget);
            validationData["reason"] = reason;
            AppDiagnostics.Warn("target_validation_failed", validationData);
        }

        _lastExternalForegroundWindow = nint.Zero;
        SetForegroundWindowState("Current app", hasExternalForegroundWindow: false);

        var clearedData = previousTarget == nint.Zero
            ? new Dictionary<string, string?>()
            : BuildWindowDiagnostics("target", previousTarget);
        clearedData["reason"] = reason;
        AppDiagnostics.Warn("target_cleared_stale", clearedData);
    }

    private void SetForegroundWindowState(string title, bool hasExternalForegroundWindow)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Current app" : title;
        var previousTitle = _currentForegroundWindowTitle;
        var previousHasExternalForegroundWindow = _hasExternalForegroundWindow;
        var titleChanged = !string.Equals(normalizedTitle, _currentForegroundWindowTitle, StringComparison.Ordinal);
        var availabilityChanged = _hasExternalForegroundWindow != hasExternalForegroundWindow;
        if (!titleChanged && !availabilityChanged)
        {
            return;
        }

        _currentForegroundWindowTitle = normalizedTitle;
        _hasExternalForegroundWindow = hasExternalForegroundWindow;
        AppDiagnostics.Info(
            "foreground_state_changed",
            new Dictionary<string, string?>
            {
                ["previousTitle"] = previousTitle,
                ["newTitle"] = _currentForegroundWindowTitle,
                ["previousHasExternalForegroundWindow"] = previousHasExternalForegroundWindow.ToString(),
                ["hasExternalForegroundWindow"] = _hasExternalForegroundWindow.ToString()
            });
        ForegroundWindowChanged?.Invoke(this, EventArgs.Empty);
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

    private static string NormalizeModifierLabel(string? modifierLabel)
    {
        if (string.IsNullOrWhiteSpace(modifierLabel))
        {
            return "Ctrl+Shift";
        }

        return modifierLabel.Trim();
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

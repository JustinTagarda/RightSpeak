using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Interop;
using RightSpeak.Interop;
using RightSpeak.Models;
using RightSpeak.Services;

namespace RightSpeak.WindowsIntegration;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int ReadSelectedHotkeyId = 0x1000;
    private const int StopHotkeyId = 0x1001;
    private const int ReadTypedTextHotkeyId = 0x1002;
    private const HotKeyModifiers ReadSelectedModifiers = HotKeyModifiers.Control | HotKeyModifiers.Shift;
    private const HotKeyModifiers ReadTypedTextModifiers = HotKeyModifiers.Control | HotKeyModifiers.Shift;
    private const HotKeyModifiers StopModifiers = HotKeyModifiers.Control | HotKeyModifiers.Shift;

    private readonly IHotkeySettingsService _hotkeySettingsService;
    private readonly HashSet<int> _registeredHotkeys = new();
    private HwndSource? _hwndSource;
    private nint _windowHandle;
    private bool _disposed;

    public WindowsGlobalHotkeyService(IHotkeySettingsService hotkeySettingsService)
    {
        _hotkeySettingsService = hotkeySettingsService;
    }

    public event EventHandler? ReadSelectedHotkeyPressed;
    public event EventHandler? ReadTypedTextHotkeyPressed;
    public event EventHandler? StopHotkeyPressed;
    public string LastRegistrationStatus { get; private set; } = "Hotkeys are not registered.";

    public bool RegisterHotkeys(nint windowHandle)
    {
        ThrowIfDisposed();

        if (windowHandle == nint.Zero)
        {
            LastRegistrationStatus = "Hotkey registration failed: invalid window handle.";
            AppDiagnostics.Warn(
                "hotkey_registration_failed_invalid_window",
                new Dictionary<string, string?>
                {
                    ["status"] = LastRegistrationStatus
                });
            return false;
        }

        _windowHandle = windowHandle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WndProc);

        return RefreshHotkeys();
    }

    public bool RefreshHotkeys()
    {
        ThrowIfDisposed();

        if (_windowHandle == nint.Zero)
        {
            LastRegistrationStatus = "Hotkey refresh failed: no active window handle.";
            AppDiagnostics.Warn(
                "hotkey_refresh_failed_no_window",
                new Dictionary<string, string?>
                {
                    ["status"] = LastRegistrationStatus
                });
            return false;
        }

        UnregisterRegisteredHotkeys();
        var configuration = _hotkeySettingsService.BuildConfiguration();
        return RegisterConfiguredHotkeys(configuration);
    }

    public void UnregisterHotkeys()
    {
        UnregisterRegisteredHotkeys();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        _windowHandle = nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterHotkeys();
        _disposed = true;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != HotKeyInterop.WmHotKey)
        {
            return nint.Zero;
        }

        var hotkeyId = wParam.ToInt32();
        if (hotkeyId == ReadSelectedHotkeyId)
        {
            ReadSelectedHotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
            return nint.Zero;
        }

        if (hotkeyId == ReadTypedTextHotkeyId)
        {
            ReadTypedTextHotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
            return nint.Zero;
        }

        if (hotkeyId == StopHotkeyId)
        {
            StopHotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return nint.Zero;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsGlobalHotkeyService));
        }
    }

    private bool RegisterConfiguredHotkeys(HotkeyConfiguration configuration)
    {
        var readSelectedRegistered = HotKeyInterop.RegisterHotKey(_windowHandle, ReadSelectedHotkeyId, (uint)ReadSelectedModifiers, configuration.ReadSelectedVirtualKey);
        if (readSelectedRegistered)
        {
            _registeredHotkeys.Add(ReadSelectedHotkeyId);
        }

        var readTypedTextRegistered = HotKeyInterop.RegisterHotKey(_windowHandle, ReadTypedTextHotkeyId, (uint)ReadTypedTextModifiers, configuration.ReadTypedTextVirtualKey);
        if (readTypedTextRegistered)
        {
            _registeredHotkeys.Add(ReadTypedTextHotkeyId);
        }

        var stopRegistered = HotKeyInterop.RegisterHotKey(_windowHandle, StopHotkeyId, (uint)StopModifiers, configuration.StopVirtualKey);
        if (stopRegistered)
        {
            _registeredHotkeys.Add(StopHotkeyId);
        }

        LastRegistrationStatus = BuildRegistrationStatusMessage(configuration, readSelectedRegistered, readTypedTextRegistered, stopRegistered);
        if (readSelectedRegistered && readTypedTextRegistered && stopRegistered)
        {
            AppDiagnostics.Info(
                "hotkey_registration_success",
                new Dictionary<string, string?>
                {
                    ["status"] = LastRegistrationStatus
                });
        }
        else
        {
            AppDiagnostics.Warn(
                "hotkey_registration_partial_failure",
                new Dictionary<string, string?>
                {
                    ["status"] = LastRegistrationStatus
                });
        }

        return readSelectedRegistered && readTypedTextRegistered && stopRegistered;
    }

    private void UnregisterRegisteredHotkeys()
    {
        if (_windowHandle == nint.Zero)
        {
            return;
        }

        foreach (var hotkeyId in _registeredHotkeys)
        {
            HotKeyInterop.UnregisterHotKey(_windowHandle, hotkeyId);
        }

        _registeredHotkeys.Clear();
    }

    private static string BuildRegistrationStatusMessage(
        HotkeyConfiguration configuration,
        bool readSelectedRegistered,
        bool readTypedTextRegistered,
        bool stopRegistered)
    {
        if (readSelectedRegistered && readTypedTextRegistered && stopRegistered)
        {
            return $"Hotkeys registered: selected Ctrl+Shift+{(char)configuration.ReadSelectedVirtualKey}, typed Ctrl+Shift+{(char)configuration.ReadTypedTextVirtualKey}, stop Ctrl+Shift+{(char)configuration.StopVirtualKey}.";
        }

        var failures = new StringBuilder("Hotkey registration partial failure:");
        if (!readSelectedRegistered)
        {
            failures.Append($" selected Ctrl+Shift+{(char)configuration.ReadSelectedVirtualKey};");
        }

        if (!readTypedTextRegistered)
        {
            failures.Append($" typed Ctrl+Shift+{(char)configuration.ReadTypedTextVirtualKey};");
        }

        if (!stopRegistered)
        {
            failures.Append($" stop Ctrl+Shift+{(char)configuration.StopVirtualKey};");
        }

        failures.Append(" Buttons and tray actions remain available.");
        return failures.ToString();
    }
}

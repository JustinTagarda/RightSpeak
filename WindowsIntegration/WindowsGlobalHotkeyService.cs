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
    private const int ReadDocumentHotkeyId = 0x1002;
    private const int StopHotkeyId = 0x1003;

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
    public event EventHandler? ReadDocumentHotkeyPressed;
    public event EventHandler? StopHotkeyPressed;
    public string LastRegistrationStatus { get; private set; } = "Hotkeys are not registered.";

    public bool RegisterHotkeys(nint windowHandle)
    {
        ThrowIfDisposed();

        if (windowHandle == nint.Zero)
        {
            LastRegistrationStatus = "Hotkeys couldn't be turned on right now.";
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
            LastRegistrationStatus = "Hotkeys couldn't be updated right now.";
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
        try
        {
            if (msg != HotKeyInterop.WmHotKey)
            {
                return nint.Zero;
            }

            var hotkeyId = wParam.ToInt32();
            if (hotkeyId == ReadSelectedHotkeyId)
            {
                RaiseHotkeyEvent(ReadSelectedHotkeyPressed, "read_selected");
                handled = true;
                return nint.Zero;
            }

            if (hotkeyId == ReadDocumentHotkeyId)
            {
                RaiseHotkeyEvent(ReadDocumentHotkeyPressed, "read_document");
                handled = true;
                return nint.Zero;
            }

            if (hotkeyId == StopHotkeyId)
            {
                RaiseHotkeyEvent(StopHotkeyPressed, "stop");
                handled = true;
            }

            return nint.Zero;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "hotkey_wndproc_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message,
                    ["msg"] = msg.ToString(),
                    ["wParam"] = wParam.ToString(),
                    ["lParam"] = lParam.ToString(),
                    ["hwnd"] = hwnd.ToString("X")
                });
            handled = false;
            return nint.Zero;
        }
    }

    private void RaiseHotkeyEvent(EventHandler? handler, string hotkeyName)
    {
        if (handler is null)
        {
            return;
        }

        try
        {
            handler.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "hotkey_event_handler_failed",
                new Dictionary<string, string?>
                {
                    ["hotkey"] = hotkeyName,
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
        }
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
        var readSelectedRegistered = HotKeyInterop.RegisterHotKey(_windowHandle, ReadSelectedHotkeyId, (uint)configuration.Modifiers, configuration.ReadSelectedVirtualKey);
        if (readSelectedRegistered)
        {
            _registeredHotkeys.Add(ReadSelectedHotkeyId);
        }

        var readDocumentRegistered = HotKeyInterop.RegisterHotKey(_windowHandle, ReadDocumentHotkeyId, (uint)configuration.Modifiers, configuration.ReadDocumentVirtualKey);
        if (readDocumentRegistered)
        {
            _registeredHotkeys.Add(ReadDocumentHotkeyId);
        }

        var stopRegistered = HotKeyInterop.RegisterHotKey(_windowHandle, StopHotkeyId, (uint)configuration.Modifiers, configuration.StopVirtualKey);
        if (stopRegistered)
        {
            _registeredHotkeys.Add(StopHotkeyId);
        }

        LastRegistrationStatus = BuildRegistrationStatusMessage(configuration, readSelectedRegistered, readDocumentRegistered, stopRegistered);
        if (readSelectedRegistered && readDocumentRegistered && stopRegistered)
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

        return readSelectedRegistered && readDocumentRegistered && stopRegistered;
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
        bool readDocumentRegistered,
        bool stopRegistered)
    {
        if (readSelectedRegistered && readDocumentRegistered && stopRegistered)
        {
            var modifierLabel = GetModifierLabel(configuration.Modifiers);
            return $"Hotkeys registered: selected {modifierLabel}+{(char)configuration.ReadSelectedVirtualKey}, document {modifierLabel}+{(char)configuration.ReadDocumentVirtualKey}, stop {modifierLabel}+{(char)configuration.StopVirtualKey}.";
        }

        var failures = new StringBuilder("Hotkey registration partial failure:");
        failures.Clear();
        failures.Append("Some hotkeys couldn't be turned on. Another app may already be using them.");
        return failures.ToString();
    }

    private static string GetModifierLabel(HotKeyModifiers modifiers)
    {
        return modifiers switch
        {
            HotKeyModifiers.Control | HotKeyModifiers.Alt => "Ctrl+Alt",
            HotKeyModifiers.Control | HotKeyModifiers.Shift => "Ctrl+Shift",
            HotKeyModifiers.Alt | HotKeyModifiers.Shift => "Alt+Shift",
            _ => "Ctrl+Shift"
        };
    }
}

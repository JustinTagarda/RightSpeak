using System;
using System.Collections.Generic;
using System.Linq;
using RightSpeak.Interop;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class HotkeySettingsService : IHotkeySettingsService
{
    private static readonly string[] DefaultKeyOptions = Enumerable.Range('A', 26)
        .Select(letter => ((char)letter).ToString())
        .ToArray();
    private const string DefaultReadSelectedKey = "S";
    private const string DefaultReadParagraphKey = "P";
    private const string DefaultReadDocumentKey = "D";
    private const string DefaultStopKey = "X";

    private readonly IAppSettingsService _appSettingsService;
    private HotkeyModifierPreset _modifierPreset;
    private string _readSelectedKey;
    private string _readParagraphKey;
    private string _readDocumentKey;
    private string _stopKey;

    public HotkeySettingsService(IAppSettingsService appSettingsService)
    {
        _appSettingsService = appSettingsService;
        AvailableKeyOptions = DefaultKeyOptions;
        var current = _appSettingsService.Current;

        _modifierPreset = ParseModifierPreset(current.HotkeyModifierPreset);
        _readSelectedKey = NormalizeOrDefault(current.ReadSelectedHotkeyKey, DefaultReadSelectedKey);
        _readParagraphKey = NormalizeOrDefault(current.ReadParagraphHotkeyKey, DefaultReadParagraphKey);
        _readDocumentKey = NormalizeOrDefault(current.ReadDocumentHotkeyKey, DefaultReadDocumentKey);
        _stopKey = NormalizeOrDefault(current.StopHotkeyKey, DefaultStopKey);

        var needsMigration =
            !string.Equals(current.HotkeyModifierPreset, _modifierPreset.ToString(), StringComparison.Ordinal) ||
            !string.Equals(current.ReadSelectedHotkeyKey, _readSelectedKey, StringComparison.Ordinal) ||
            !string.Equals(current.ReadParagraphHotkeyKey, _readParagraphKey, StringComparison.Ordinal) ||
            !string.Equals(current.ReadDocumentHotkeyKey, _readDocumentKey, StringComparison.Ordinal) ||
            !string.Equals(current.StopHotkeyKey, _stopKey, StringComparison.Ordinal);

        PersistInMemorySettings();
        if (needsMigration)
        {
            _appSettingsService.Save();
        }
    }

    public IReadOnlyList<string> AvailableKeyOptions { get; }
    public HotkeyModifierPreset ModifierPreset
    {
        get => _modifierPreset;
        set => _modifierPreset = value;
    }

    public string ReadSelectedKey
    {
        get => _readSelectedKey;
        set => _readSelectedKey = NormalizeOrDefault(value, DefaultReadSelectedKey);
    }

    public string ReadParagraphKey
    {
        get => _readParagraphKey;
        set => _readParagraphKey = NormalizeOrDefault(value, DefaultReadParagraphKey);
    }

    public string ReadDocumentKey
    {
        get => _readDocumentKey;
        set => _readDocumentKey = NormalizeOrDefault(value, DefaultReadDocumentKey);
    }

    public string StopKey
    {
        get => _stopKey;
        set => _stopKey = NormalizeOrDefault(value, DefaultStopKey);
    }

    public bool Save()
    {
        if (!HasUniqueKeys())
        {
            return false;
        }

        PersistInMemorySettings();
        _appSettingsService.Save();
        return true;
    }

    public HotkeyConfiguration BuildConfiguration()
    {
        return new HotkeyConfiguration(
            modifiers: ToModifiers(_modifierPreset),
            readSelectedVirtualKey: ToVirtualKey(_readSelectedKey),
            readParagraphVirtualKey: ToVirtualKey(_readParagraphKey),
            readDocumentVirtualKey: ToVirtualKey(_readDocumentKey),
            stopVirtualKey: ToVirtualKey(_stopKey));
    }

    private bool HasUniqueKeys()
    {
        return !string.Equals(_readSelectedKey, _readParagraphKey, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(_readSelectedKey, _readDocumentKey, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(_readSelectedKey, _stopKey, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(_readParagraphKey, _readDocumentKey, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(_readParagraphKey, _stopKey, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(_readDocumentKey, _stopKey, StringComparison.OrdinalIgnoreCase);
    }

    private void PersistInMemorySettings()
    {
        _appSettingsService.Current.HotkeyModifierPreset = _modifierPreset.ToString();
        _appSettingsService.Current.ReadSelectedHotkeyKey = _readSelectedKey;
        _appSettingsService.Current.ReadParagraphHotkeyKey = _readParagraphKey;
        _appSettingsService.Current.ReadDocumentHotkeyKey = _readDocumentKey;
        _appSettingsService.Current.StopHotkeyKey = _stopKey;
    }

    private static string NormalizeOrDefault(string? key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return fallback;
        }

        var normalized = key.Trim().ToUpperInvariant();
        return normalized.Length == 1 && normalized[0] >= 'A' && normalized[0] <= 'Z'
            ? normalized
            : fallback;
    }

    private static uint ToVirtualKey(string key)
    {
        return key[0];
    }

    private static HotkeyModifierPreset ParseModifierPreset(string? value)
    {
        if (Enum.TryParse<HotkeyModifierPreset>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return HotkeyModifierPreset.AltShift;
    }

    private static HotKeyModifiers ToModifiers(HotkeyModifierPreset preset)
    {
        return preset switch
        {
            HotkeyModifierPreset.CtrlShift => HotKeyModifiers.Control | HotKeyModifiers.Shift,
            HotkeyModifierPreset.CtrlAlt => HotKeyModifiers.Control | HotKeyModifiers.Alt,
            _ => HotKeyModifiers.Alt | HotKeyModifiers.Shift
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class HotkeySettingsService : IHotkeySettingsService
{
    private static readonly string[] DefaultKeyOptions = Enumerable.Range('A', 26)
        .Select(letter => ((char)letter).ToString())
        .ToArray();

    private readonly IAppSettingsService _appSettingsService;
    private string _readSelectedKey;
    private string _readTypedTextKey;
    private string _stopKey;

    public HotkeySettingsService(IAppSettingsService appSettingsService)
    {
        _appSettingsService = appSettingsService;
        AvailableKeyOptions = DefaultKeyOptions;
        _readSelectedKey = NormalizeOrDefault(_appSettingsService.Current.ReadSelectedHotkeyKey, "R");
        _readTypedTextKey = NormalizeOrDefault(_appSettingsService.Current.ReadTypedTextHotkeyKey, "T");
        _stopKey = NormalizeOrDefault(_appSettingsService.Current.StopHotkeyKey, "X");
        PersistInMemorySettings();
    }

    public IReadOnlyList<string> AvailableKeyOptions { get; }

    public string ReadSelectedKey
    {
        get => _readSelectedKey;
        set => _readSelectedKey = NormalizeOrDefault(value, "R");
    }

    public string ReadTypedTextKey
    {
        get => _readTypedTextKey;
        set => _readTypedTextKey = NormalizeOrDefault(value, "T");
    }

    public string StopKey
    {
        get => _stopKey;
        set => _stopKey = NormalizeOrDefault(value, "X");
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
            readSelectedVirtualKey: ToVirtualKey(_readSelectedKey),
            readTypedTextVirtualKey: ToVirtualKey(_readTypedTextKey),
            stopVirtualKey: ToVirtualKey(_stopKey));
    }

    private bool HasUniqueKeys()
    {
        return !string.Equals(_readSelectedKey, _readTypedTextKey, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(_readSelectedKey, _stopKey, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(_readTypedTextKey, _stopKey, StringComparison.OrdinalIgnoreCase);
    }

    private void PersistInMemorySettings()
    {
        _appSettingsService.Current.ReadSelectedHotkeyKey = _readSelectedKey;
        _appSettingsService.Current.ReadTypedTextHotkeyKey = _readTypedTextKey;
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
}

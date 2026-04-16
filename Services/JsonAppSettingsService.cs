using System;
using System.IO;
using System.Text.Json;
using RightSpeak.Models;

namespace RightSpeak.Services;

public sealed class JsonAppSettingsService : IAppSettingsService
{
    private const string CorruptBackupExtension = ".corrupt";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public JsonAppSettingsService()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightSpeak");
        Directory.CreateDirectory(settingsDirectory);

        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        Current = LoadOrDefault(_settingsPath);
    }

    public AppSettings Current { get; }

    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, SerializerOptions);
        var tempPath = $"{_settingsPath}.tmp";

        File.WriteAllText(tempPath, json);
        if (File.Exists(_settingsPath))
        {
            File.Replace(tempPath, _settingsPath, null, ignoreMetadataErrors: true);
            AppDiagnostics.Info("settings_saved");
            return;
        }

        File.Move(tempPath, _settingsPath);
        AppDiagnostics.Info("settings_saved");
    }

    private static AppSettings LoadOrDefault(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? HandleCorruptSettingsFile(settingsPath);
        }
        catch
        {
            return HandleCorruptSettingsFile(settingsPath);
        }
    }

    private static AppSettings HandleCorruptSettingsFile(string settingsPath)
    {
        TryBackupCorruptSettings(settingsPath);
        return new AppSettings();
    }

    private static void TryBackupCorruptSettings(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return;
            }

            var backupPath = $"{settingsPath}.{DateTime.UtcNow:yyyyMMddHHmmss}{CorruptBackupExtension}";
            File.Copy(settingsPath, backupPath, overwrite: false);
            AppDiagnostics.Warn(
                "settings_corrupt_backup_created",
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["backupPath"] = backupPath
                });
        }
        catch
        {
            AppDiagnostics.Warn("settings_corrupt_backup_failed");
            // Best effort only; fallback remains default settings.
        }
    }
}

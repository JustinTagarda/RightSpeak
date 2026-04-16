using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RightSpeak.Services;

internal static class AppDiagnostics
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };
    private static readonly string LogFilePath = BuildLogFilePath();

    public static void Info(string eventName, IReadOnlyDictionary<string, string?>? data = null)
    {
        Write("INFO", eventName, data);
    }

    public static void Warn(string eventName, IReadOnlyDictionary<string, string?>? data = null)
    {
        Write("WARN", eventName, data);
    }

    public static void Error(string eventName, IReadOnlyDictionary<string, string?>? data = null)
    {
        Write("ERROR", eventName, data);
    }

    private static void Write(string level, string eventName, IReadOnlyDictionary<string, string?>? data)
    {
        try
        {
            var payload = new
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                level,
                eventName,
                data
            };

            var line = JsonSerializer.Serialize(payload, SerializerOptions);
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics failures should never affect primary app behavior.
        }
    }

    private static string BuildLogFilePath()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightSpeak",
            "logs");
        return Path.Combine(logDirectory, "rightspeak.log");
    }
}

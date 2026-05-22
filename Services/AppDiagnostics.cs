using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using Windows.ApplicationModel;

namespace RightSpeak.Services;

internal static class AppDiagnostics
{
    private sealed class ScopeState : IDisposable
    {
        private readonly Dictionary<string, string?>? _previous;
        private bool _disposed;

        public ScopeState(Dictionary<string, string?>? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            ScopeData.Value = _previous;
            _disposed = true;
        }
    }

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };
    private static readonly bool IsLoggingEnabled = BuildIsLoggingEnabled();
    private static readonly string LogFilePath = BuildLogFilePath();
    private static readonly AsyncLocal<Dictionary<string, string?>?> ScopeData = new();

    public static IDisposable BeginScope(IReadOnlyDictionary<string, string?> scopeData)
    {
        var previous = ScopeData.Value;
        var merged = previous is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(previous, StringComparer.Ordinal);

        foreach (var pair in scopeData.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)))
        {
            merged[pair.Key] = pair.Value;
        }

        ScopeData.Value = merged;
        return new ScopeState(previous);
    }

    public static IReadOnlyDictionary<string, string?>? CaptureScope()
    {
        if (ScopeData.Value is null)
        {
            return null;
        }

        return new Dictionary<string, string?>(ScopeData.Value, StringComparer.Ordinal);
    }

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
        if (!IsLoggingEnabled)
        {
            return;
        }

        try
        {
            Dictionary<string, string?>? mergedData = null;
            if (ScopeData.Value is not null)
            {
                mergedData = new Dictionary<string, string?>(ScopeData.Value, StringComparer.Ordinal);
            }

            if (data is not null)
            {
                mergedData ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var pair in data.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)))
                {
                    mergedData[pair.Key] = pair.Value;
                }
            }

            var payload = new
            {
                timestampUtc = DateTime.UtcNow.ToString("O"),
                level,
                eventName,
                data = mergedData
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
        // Debug diagnostics live beside the launched executable.
        return Path.Combine(AppContext.BaseDirectory, "rightspeak.log");
    }

    private static bool BuildIsLoggingEnabled()
    {
        if (!BuildConfiguration.IsDebugDiagnosticsEnabled)
        {
            return false;
        }

        // Store-packaged installs should never emit debug diagnostics logs.
        return !IsPackagedProcess();
    }

    private static bool IsPackagedProcess()
    {
        try
        {
            _ = Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

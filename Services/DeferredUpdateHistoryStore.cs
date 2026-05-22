using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RightSpeak.Services;

internal sealed class DeferredUpdateHistoryStore : IDeferredUpdateHistoryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _stateFilePath;

    public DeferredUpdateHistoryStore()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppContext.BaseDirectory;
        }

        _stateFilePath = Path.Combine(root, "RightSpeak", "update-history.json");
    }

    public DeferredUpdateState? TryLoad()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<DeferredUpdateState>(json, SerializerOptions);
            if (state is null)
            {
                TryDeleteStateFile();
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (state.IsStale(now))
            {
                AppDiagnostics.Info(
                    "deferred_update_history_stale",
                    new Dictionary<string, string?>
                    {
                        ["packageIdentitySnapshot"] = state.PackageIdentitySnapshot,
                        ["hasPendingInstall"] = state.HasPendingInstall.ToString(),
                        ["lastInstallAttemptFailed"] = state.LastInstallAttemptFailed.ToString()
                    });
                TryDeleteStateFile();
                return null;
            }

            return state.MarkObserved(now);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "deferred_update_history_load_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            TryDeleteStateFile();
            return null;
        }
    }

    public async Task<bool> SaveAsync(DeferredUpdateState state, CancellationToken cancellationToken = default)
    {
        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            await File.WriteAllTextAsync(
                _stateFilePath,
                JsonSerializer.Serialize(state, SerializerOptions),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Error(
                "deferred_update_history_save_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message,
                    ["packageIdentitySnapshot"] = state.PackageIdentitySnapshot
                });
            return false;
        }
    }

    public Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(TryDeleteStateFile());
    }

    private bool TryDeleteStateFile()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Warn(
                "deferred_update_history_clear_failed",
                new Dictionary<string, string?>
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["message"] = ex.Message
                });
            return false;
        }
    }
}

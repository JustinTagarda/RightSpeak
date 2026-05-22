using System;

namespace RightSpeak.Services;

internal sealed record DeferredUpdateState
{
    private static readonly TimeSpan PendingStateRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailureStateRetention = TimeSpan.FromDays(14);
    private static readonly TimeSpan CheckStateRetention = TimeSpan.FromDays(7);

    public static DeferredUpdateState CreatePending(
        string packageIdentitySnapshot,
        string installedVersion,
        string? availableVersion,
        DateTimeOffset createdUtc)
    {
        return new DeferredUpdateState
        {
            PackageIdentitySnapshot = packageIdentitySnapshot ?? string.Empty,
            InstalledVersion = installedVersion ?? string.Empty,
            AvailableVersion = availableVersion,
            CreatedUtc = createdUtc,
            LastObservedUtc = createdUtc,
            LastCheckUtc = createdUtc,
            HasPendingInstall = true
        };
    }

    public static DeferredUpdateState CreateChecked(
        string installedVersion,
        DateTimeOffset checkedUtc)
    {
        return new DeferredUpdateState
        {
            InstalledVersion = installedVersion ?? string.Empty,
            CreatedUtc = checkedUtc,
            LastObservedUtc = checkedUtc,
            LastCheckUtc = checkedUtc
        };
    }

    public string PackageIdentitySnapshot { get; init; } = string.Empty;
    public string InstalledVersion { get; init; } = string.Empty;
    public string? AvailableVersion { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? LastObservedUtc { get; init; }
    public DateTimeOffset? LastCheckUtc { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastFailureUtc { get; init; }
    public DateTimeOffset? RetryNotBeforeUtc { get; init; }
    public int FailureCount { get; init; }
    public bool HasPendingInstall { get; init; }
    public bool LastInstallAttemptFailed { get; init; }
    public string? LastFailureMessage { get; init; }

    public bool CanAttemptInstall(DateTimeOffset now)
    {
        return RetryNotBeforeUtc is null || now >= RetryNotBeforeUtc.Value;
    }

    public bool IsStale(DateTimeOffset now)
    {
        if (HasPendingInstall)
        {
            return now - CreatedUtc > PendingStateRetention;
        }

        if (LastInstallAttemptFailed && LastFailureUtc is not null)
        {
            return now - LastFailureUtc.Value > FailureStateRetention;
        }

        if (LastCheckUtc is not null)
        {
            return now - LastCheckUtc.Value > CheckStateRetention;
        }

        return true;
    }

    public DeferredUpdateState MarkObserved(DateTimeOffset observedUtc)
    {
        return this with
        {
            LastObservedUtc = observedUtc
        };
    }

    public DeferredUpdateState MarkChecked(DateTimeOffset checkedUtc)
    {
        return this with
        {
            LastCheckUtc = checkedUtc,
            LastObservedUtc = checkedUtc
        };
    }

    public DeferredUpdateState MarkInstallAttempt(DateTimeOffset attemptUtc)
    {
        return this with
        {
            LastAttemptUtc = attemptUtc
        };
    }

    public DeferredUpdateState MarkInstallSuccess(DateTimeOffset completedUtc)
    {
        return this with
        {
            HasPendingInstall = false,
            LastInstallAttemptFailed = false,
            LastAttemptUtc = completedUtc,
            LastFailureUtc = null,
            RetryNotBeforeUtc = null,
            LastFailureMessage = null
        };
    }

    public DeferredUpdateState MarkInstallFailure(DateTimeOffset failedUtc, string failureMessage, TimeSpan retryDelay)
    {
        return this with
        {
            HasPendingInstall = false,
            LastInstallAttemptFailed = true,
            LastAttemptUtc = failedUtc,
            LastFailureUtc = failedUtc,
            RetryNotBeforeUtc = failedUtc + retryDelay,
            FailureCount = FailureCount + 1,
            LastFailureMessage = string.IsNullOrWhiteSpace(failureMessage) ? null : failureMessage
        };
    }
}

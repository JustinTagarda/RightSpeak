using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RightSpeak.Services;

internal static class PiperRuntimeEnvironment
{
    public const string PreinstalledVoiceId = "en_US-ljspeech-high";
    private static Func<string>? _baseDirectoryOverrideForTests;
    private static Func<string>? _piperRootDirectoryOverrideForTests;
    private static Func<Architecture>? _processArchitectureOverrideForTests;

    public static bool IsRuntimeSupportedOnCurrentArchitecture(out string? failureReason)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            failureReason = "Piper installs require a 64-bit Windows build.";
            return false;
        }

        if (GetCurrentProcessArchitecture() == Architecture.X64)
        {
            failureReason = null;
            return true;
        }

        failureReason = "Piper installs are currently available only on x64 Windows builds.";
        return false;
    }

    public static string GetDefaultPiperRootDirectory()
    {
        if (_piperRootDirectoryOverrideForTests is not null)
        {
            return _piperRootDirectoryOverrideForTests();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RightSpeak",
            "Piper");
    }

    internal static string GetBaseDirectory()
    {
        return _baseDirectoryOverrideForTests?.Invoke() ?? AppContext.BaseDirectory;
    }

    public static Architecture GetCurrentProcessArchitecture()
    {
        return _processArchitectureOverrideForTests?.Invoke() ?? RuntimeInformation.ProcessArchitecture;
    }

    public static string GetRuntimeMoniker(Architecture architecture)
    {
        return architecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm => "win-arm",
            _ => string.Empty
        };
    }

    internal static IDisposable UseBaseDirectoryForTests(string baseDirectory)
    {
        _baseDirectoryOverrideForTests = () => baseDirectory;
        return new TestOverrideScope(
            () => _baseDirectoryOverrideForTests = null);
    }

    internal static IDisposable UseProcessArchitectureForTests(Architecture architecture)
    {
        _processArchitectureOverrideForTests = () => architecture;
        return new TestOverrideScope(
            () => _processArchitectureOverrideForTests = null);
    }

    internal static IDisposable UsePiperRootDirectoryForTests(string piperRootDirectory)
    {
        _piperRootDirectoryOverrideForTests = () => piperRootDirectory;
        return new TestOverrideScope(
            () => _piperRootDirectoryOverrideForTests = null);
    }

    public static string GetVoicesDirectory(string piperRootDirectory)
    {
        return Path.Combine(piperRootDirectory, "voices");
    }

    public static string GetDownloadsDirectory(string piperRootDirectory)
    {
        return Path.Combine(piperRootDirectory, "downloads");
    }

    public static string GetManifestPath(string piperRootDirectory)
    {
        return Path.Combine(piperRootDirectory, "installed-voices.json");
    }

    public static string GetActiveRuntimeDirectory(string piperRootDirectory)
    {
        return Path.Combine(piperRootDirectory, "runtime");
    }

    public static string GetPackagedRuntimeDirectory(string baseDirectory)
    {
        return GetPackagedRuntimeDirectory(baseDirectory, GetCurrentProcessArchitecture());
    }

    public static string GetPackagedRuntimeDirectory(string baseDirectory, Architecture architecture)
    {
        return Path.Combine(baseDirectory, "Resources", "Piper", "runtime");
    }

    public static IEnumerable<string> EnumeratePackagedRuntimeDirectories(string baseDirectory, Architecture architecture)
    {
        if (architecture == Architecture.X64)
        {
            yield return Path.Combine(baseDirectory, "Resources", "Piper", "runtime");
        }
    }

    public static string GetVersionedRuntimeDirectory(string piperRootDirectory, string version)
    {
        return Path.Combine(
            piperRootDirectory,
            "runtimes",
            SanitizeDirectoryName(version));
    }

    public static IEnumerable<string> EnumeratePiperExecutableCandidates(string piperRootDirectory, string baseDirectory)
    {
        var architecture = GetCurrentProcessArchitecture();
        yield return Path.Combine(GetActiveRuntimeDirectory(piperRootDirectory), "piper.exe");
        yield return Path.Combine(piperRootDirectory, "piper.exe");
        yield return Path.Combine(piperRootDirectory, "piper", "piper.exe");

        foreach (var runtimeDirectory in EnumeratePackagedRuntimeDirectories(baseDirectory, architecture))
        {
            yield return Path.Combine(runtimeDirectory, "piper.exe");
        }

        if (architecture == Architecture.X64)
        {
            yield return Path.Combine(baseDirectory, "Resources", "Piper", "piper", "piper.exe");
            yield return Path.Combine(baseDirectory, "Piper", "runtime", "piper.exe");
            yield return Path.Combine(baseDirectory, "Piper", "piper", "piper.exe");
        }
    }

    public static IEnumerable<string> EnumerateVoiceDirectoryCandidates(string piperRootDirectory, string baseDirectory)
    {
        yield return GetVoicesDirectory(piperRootDirectory);
        yield return Path.Combine(baseDirectory, "Resources", "Piper", "voices");
        yield return Path.Combine(baseDirectory, "Piper", "voices");
        yield return piperRootDirectory;
        yield return Path.Combine(baseDirectory, "Resources", "Piper");
        yield return Path.Combine(baseDirectory, "Piper");
    }

    public static bool HasRequiredRuntimeItems(string runtimeDirectory, out List<string> missingItems)
    {
        missingItems = [];
        foreach (var dependency in new[]
                 {
                     "piper.exe",
                     "onnxruntime.dll",
                     "espeak-ng.dll",
                     "piper_phonemize.dll"
                 })
        {
            if (!File.Exists(Path.Combine(runtimeDirectory, dependency)))
            {
                missingItems.Add(dependency);
            }
        }

        if (!Directory.Exists(Path.Combine(runtimeDirectory, "espeak-ng-data")))
        {
            missingItems.Add("espeak-ng-data");
        }

        return missingItems.Count == 0;
    }

    private static string SanitizeDirectoryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidCharacter, '_');
        }

        return value.Trim();
    }

    private sealed class TestOverrideScope : IDisposable
    {
        private readonly Action _clear;
        private bool _disposed;

        public TestOverrideScope(Action clear)
        {
            _clear = clear;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _clear();
            _disposed = true;
        }
    }
}

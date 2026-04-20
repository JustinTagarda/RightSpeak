using System;
using System.Collections.Generic;

namespace RightSpeak.Models;

public static class AppThemes
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string WindowsSettings = "Windows settings";

    public static readonly IReadOnlyList<string> Options = new[]
    {
        WindowsSettings,
        Light,
        Dark
    };

    public static string Normalize(string? theme)
    {
        if (string.Equals(theme, WindowsSettings, StringComparison.OrdinalIgnoreCase))
        {
            return WindowsSettings;
        }

        return string.Equals(theme, Dark, StringComparison.OrdinalIgnoreCase)
            ? Dark
            : Light;
    }
}

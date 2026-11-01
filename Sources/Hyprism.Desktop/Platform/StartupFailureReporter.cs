// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.RegularExpressions;
using Hyprism.Core.Infrastructure;

namespace Hyprism.Desktop.Platform;

/// <summary>Writes a best-effort startup failure report even when normal logging is unavailable.</summary>
internal static partial class StartupFailureReporter
{
    public static string Write(Exception exception, string phase, string? appDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var path = Path.Combine(appDirectory is null ? GetLogDirectory() : Path.Combine(Path.GetFullPath(appDirectory), "Logs"), "startup-failure.log");
        var details = Redact(exception.ToString());
        var report = $"Startup phase: {phase}{Environment.NewLine}Exception: {details}{Environment.NewLine}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, report);
            return path;
        }
        catch
        {
            return path;
        }
    }

    private static string GetLogDirectory()
    {
        try
        {
            var appDir = LauncherUtilities.GetEffectiveAppDir();
            return Path.Combine(appDir, "Logs");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "Hyprism", "Logs");
        }
    }

    private static string Redact(string value)
    {
        value = SecretQueryParameter().Replace(value, "$1=[redacted]");
        value = SecretJsonValue().Replace(value, "$1\"[redacted]\"");
        value = SecretAssignment().Replace(value, "$1=[redacted]");
        return BearerCredential().Replace(value, "Bearer [redacted]");
    }

    [GeneratedRegex("(?i)(access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|authorization[_-]?code|code|authorization)(=|%3d)([^&\\s\\\"']+)")]
    private static partial Regex SecretQueryParameter();

    [GeneratedRegex("(?i)(\"(?:access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|authorization[_-]?code|code|authorization)\"\\s*:\\s*)\"[^\"]*\"")]
    private static partial Regex SecretJsonValue();

    [GeneratedRegex("(?i)\\b(access[_-]?token|refresh[_-]?token|id[_-]?token|client[_-]?secret|authorization[_-]?code|authorization)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("(?i)\\bBearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerCredential();
}

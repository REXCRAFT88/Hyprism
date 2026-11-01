// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Hyprism.Desktop.Platform;
using Xunit;

namespace Hyprism.Desktop.Tests;

public sealed class StartupFailureReporterTests
{
    [Fact]
    public void WritesPhaseAndRedactsCredentialLikeExceptionText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hyprism-startup-report-{Guid.NewGuid():N}");
        var path = StartupFailureReporter.Write(
            new InvalidOperationException("refresh_token=secret-value and access_token=another-secret"),
            "fixture phase",
            root);

        var report = File.ReadAllText(path);
        Assert.Contains("fixture phase", report, StringComparison.Ordinal);
        Assert.Contains("[redacted]", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", report, StringComparison.Ordinal);
        Assert.DoesNotContain("another-secret", report, StringComparison.Ordinal);
    }
}

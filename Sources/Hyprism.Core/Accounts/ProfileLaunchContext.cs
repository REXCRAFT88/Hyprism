// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

namespace Hyprism.Core.Accounts;

/// <summary>
/// Carries an explicit account profile through one asynchronous game launch.
/// </summary>
public static class ProfileLaunchContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>Gets the profile selected for the current launch operation, if any.</summary>
    public static string? ProfileId => Current.Value;

    /// <summary>Sets the profile used by the current asynchronous launch operation.</summary>
    /// <param name="profileId">The stable profile identifier to scope to this operation.</param>
    /// <returns>A scope that restores the previous profile when disposed.</returns>
    public static IDisposable Use(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var previous = Current.Value;
        Current.Value = profileId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            Current.Value = previous;
            _disposed = true;
        }
    }
}

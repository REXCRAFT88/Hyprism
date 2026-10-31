// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

namespace Hyprism.Core.Accounts;

/// <summary>
/// Carries an explicit account profile through one asynchronous game launch.
/// </summary>
public static class ProfileLaunchContext
{
    private static readonly AsyncLocal<string?> Current = new();
    private static readonly AsyncLocal<bool> IsolatedAccountData = new();

    /// <summary>Gets the profile selected for the current launch operation, if any.</summary>
    public static string? ProfileId => Current.Value;

    /// <summary>Gets whether this launch keeps account data outside the shared install.</summary>
    public static bool UsesIsolatedAccountData => IsolatedAccountData.Value;

    /// <summary>Sets the profile used by the current asynchronous launch operation.</summary>
    /// <param name="profileId">The stable profile identifier to scope to this operation.</param>
    /// <param name="useIsolatedAccountData">Whether the launch uses Together's account-private data directory.</param>
    /// <returns>A scope that restores the previous profile when disposed.</returns>
    public static IDisposable Use(string profileId, bool useIsolatedAccountData = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var previous = Current.Value;
        var previousIsolated = IsolatedAccountData.Value;
        Current.Value = profileId;
        IsolatedAccountData.Value = useIsolatedAccountData;
        return new Scope(previous, previousIsolated);
    }

    /// <summary>Returns the legacy Together account-private directory for a profile.</summary>
    /// <param name="playerUuid">The Hytale player UUID used as the account data key.</param>
    /// <param name="branch">The installed game branch.</param>
    /// <returns>The account-specific writable user data path.</returns>
    /// <exception cref="ArgumentException">The UUID or branch is invalid.</exception>
    public static string GetAccountUserDataPath(string playerUuid, string branch)
    {
        if (!Guid.TryParse(playerUuid, out var playerId))
            throw new ArgumentException("A valid Hytale player UUID is required.", nameof(playerUuid));

        var normalizedBranch = branch.Trim().ToLowerInvariant() switch
        {
            "release" => "release",
            "pre-release" => "pre-release",
            _ => throw new ArgumentException("Only the release and pre-release branches are supported.", nameof(branch))
        };

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Hytale", "SplitScreen", "Accounts", playerId.ToString("D"), normalizedBranch, "UserData");
    }

    private sealed class Scope(string? previous, bool previousIsolated) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            Current.Value = previous;
            IsolatedAccountData.Value = previousIsolated;
            _disposed = true;
        }
    }
}

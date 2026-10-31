// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Hyprism.Core.Application.Progress;
using Hyprism.Core.Game.Instances;
using Hyprism.Core.Game.Launch;
using Hyprism.Core.Accounts;
using Hyprism.Core.Infrastructure;

namespace Hyprism.Core.Game;

/// <summary>
/// UI-neutral entry point for the complete game launch workflow.
/// </summary>
public sealed class GameLaunchCoordinator(
    IGameInstallationWorkflow gameSession,
    IGameProcessTracker processService,
    IInstanceRepository instances,
    IProgressReporter progress) : IGameLaunchCoordinator
{
    private static readonly SemaphoreSlim ProfileLaunchGate = new(1, 1);
    private readonly HashSet<string> _launchingOperationIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _launchLock = new();
    private const int ExitSuccess = 0;
    private const int ErrorNotInstalled = 11;
    private const int ErrorDownloadFailed = 12;
    private const int ErrorLaunchFailed = 13;
    private const int ErrorMirrorUnreachable = 14;

    /// <inheritdoc/>
    public event EventHandler<LaunchFailedEventArgs>? LaunchFailed;

    /// <inheritdoc/>
    public async Task LaunchAsync(
        string? instanceId = null,
        AuthUriPresenter? authorizationUriPresenter = null)
        => await LaunchCoreAsync(instanceId, authorizationUriPresenter, allowSharedInstallation: false);

    private async Task LaunchCoreAsync(
        string? instanceId,
        AuthUriPresenter? authorizationUriPresenter,
        bool allowSharedInstallation)
    {
        // Install and update remain separate UI actions, so this operation launches
        // only an instance whose selected version is already installed.
        var selectedInstance = string.IsNullOrWhiteSpace(instanceId)
            ? instances.GetSelectedInstance()
            : instances.FindInstanceById(instanceId);
        using var operation = selectedInstance is null
            ? null
            : progress.BeginOperation(selectedInstance.Id);
        if (selectedInstance != null)
        {
            var profileId = allowSharedInstallation ? ProfileLaunchContext.ProfileId : null;
            var operationId = allowSharedInstallation
                ? $"{selectedInstance.Id}::{profileId}"
                : selectedInstance.Id;
            if ((allowSharedInstallation && (string.IsNullOrWhiteSpace(profileId) || processService.IsProfileRunning(profileId)))
                || (!allowSharedInstallation && processService.IsInstanceRunning(selectedInstance.Id)))
            {
                Logger.Warning("Game", allowSharedInstallation
                    ? $"Game launch request ignored - profile {profileId} is already running"
                    : $"Game launch request ignored - instance {selectedInstance.Id} is already running");
                return;
            }

            var versionPath = instances.GetInstancePathById(selectedInstance.Id);
            if (!string.IsNullOrEmpty(versionPath) && !instances.IsClientPresent(versionPath))
            {
                Logger.Warning("Game", $"Instance {selectedInstance.Id} has no game client installed");
                progress.ReportError(
                    "launch",
                    "Game not installed",
                    $"Instance '{selectedInstance.Name}' has no game installed. Click UPDATE to install.",
                    selectedInstance.Id);
                return;
            }

            if (allowSharedInstallation)
            {
                var meta = string.IsNullOrEmpty(versionPath) ? null : instances.GetInstanceMeta(versionPath);
                if (string.IsNullOrEmpty(versionPath)
                    || !instances.IsClientPresent(versionPath)
                    || meta is null
                    || meta.PendingVersion > 0
                    || meta.InstalledVersion != selectedInstance.Version
                    || !string.Equals(meta.Branch, selectedInstance.Branch, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Warning("Game", $"Together launch rejected for {selectedInstance.Id}: the installation must be complete, current, and have no pending update");
                    progress.ReportError(
                        "launch",
                        "Game installation needs attention",
                        "Install or update this instance, then retry the Together session.",
                        selectedInstance.Id);
                    return;
                }
            }

            lock (_launchLock)
            {
                if (!_launchingOperationIds.Add(operationId))
                {
                    Logger.Warning("Game", $"Game launch request ignored - operation {operationId} is already being prepared");
                    return;
                }
            }
        }

        Logger.Info("Game", "Game launch requested");

        try
        {
            var result = string.IsNullOrWhiteSpace(instanceId)
                ? await gameSession.DownloadAndLaunchAsync(authorizationUriPresenter)
                : await gameSession.DownloadAndLaunchInstanceAsync(instanceId, authorizationUriPresenter);

            if (result.Cancelled || string.Equals(result.Error, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                RaiseLaunchFailed(selectedInstance?.Id, ExitSuccess);
                return;
            }

            if (!result.Success)
            {
                var exitCode = DetermineExitCodeForError(result.Error);
                progress.ReportError(
                    "download",
                    "Failed to install game",
                    result.Error ?? "Unknown error",
                    selectedInstance?.Id);
                RaiseLaunchFailed(selectedInstance?.Id, exitCode);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Game", $"Game launch failed: {ex.Message}");
            progress.ReportError("download", "Failed to install game", ex.ToString(), selectedInstance?.Id);
            RaiseLaunchFailed(selectedInstance?.Id, ErrorLaunchFailed);
        }
        finally
        {
            if (selectedInstance is not null)
            {
                lock (_launchLock)
                    _launchingOperationIds.Remove(allowSharedInstallation
                        ? $"{selectedInstance.Id}::{ProfileLaunchContext.ProfileId}"
                        : selectedInstance.Id);
            }
        }
    }

    /// <inheritdoc/>
    public async Task LaunchProfileAsync(
        string instanceId,
        string profileId,
        AuthUriPresenter? authorizationUriPresenter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await ProfileLaunchGate.WaitAsync();
        try
        {
            using var profile = ProfileLaunchContext.Use(profileId, useIsolatedAccountData: true);
            await LaunchCoreAsync(instanceId, authorizationUriPresenter, allowSharedInstallation: true);
        }
        finally
        {
            ProfileLaunchGate.Release();
        }
    }

    private void RaiseLaunchFailed(string? instanceId, int exitCode)
        => LaunchFailed?.Invoke(this, new LaunchFailedEventArgs(instanceId, exitCode));

    private static int DetermineExitCodeForError(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return ErrorDownloadFailed;

        var normalized = error.ToLowerInvariant();

        if (normalized.Contains("not installed") || normalized.Contains("no game client"))
            return ErrorNotInstalled;

        if (normalized.Contains("mirror") &&
            (normalized.Contains("unreachable") || normalized.Contains("failed")))
            return ErrorMirrorUnreachable;

        if (normalized.Contains("launch"))
            return ErrorLaunchFailed;

        if (normalized.Contains("download"))
            return ErrorDownloadFailed;

        return ErrorDownloadFailed;
    }
}

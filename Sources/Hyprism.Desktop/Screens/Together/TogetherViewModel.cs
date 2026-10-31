// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hyprism.Core;
using Hyprism.Core.Accounts;
using Hyprism.Core.Application.Ports;
using Hyprism.Core.Game;
using Hyprism.Core.Game.Instances;
using Hyprism.Core.Game.Launch;
using Hyprism.Core.Infrastructure;
using Hyprism.Core.Models;
using Hyprism.Desktop.Localization;
using Hyprism.Desktop.Platform;

namespace Hyprism.Desktop.Screens.Together;

public sealed partial class TogetherViewModel : ObservableObject, IDisposable
{
    private readonly IProfileRepository _profiles;
    private readonly IHytaleAuthenticator _authenticator;
    private readonly IInstanceRepository _instances;
    private readonly IGameLaunchCoordinator _launchCoordinator;
    private readonly IGameProcessTracker _processTracker;
    private readonly IExternalUriLauncher _uriLauncher;
    private readonly StringLocalizer _localizer;
    private readonly string _appDir;
    private readonly string _nativeHostPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Together", "HytaleInputHost.exe");
    private readonly object _sessionGate = new();
    private readonly List<PlayerWindow> _sessionWindows = [];
    private readonly ControllerFeatureRunner _controllerFeatures = new();
    private CancellationTokenSource? _sessionCancellation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartSession))]
    [NotifyPropertyChangedFor(nameof(CanEditSession))]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPlayerCommand))]
    private bool _isStarting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartSession))]
    [NotifyPropertyChangedFor(nameof(CanEditSession))]
    [NotifyCanExecuteChangedFor(nameof(StartSessionCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddPlayerCommand))]
    private bool _isSessionRunning;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _layout = "Grid";

    [ObservableProperty]
    private TogetherDisplayOption? _selectedDisplay;

    public ObservableCollection<TogetherPlayerViewModel> Players { get; } = [];
    public ObservableCollection<TogetherControllerOption> Controllers { get; } = [];
    public ObservableCollection<TogetherEscapeOption> EscapeOptions { get; } = [];
    public ObservableCollection<TogetherColorOption> ColorOptions { get; } = [];
    public ObservableCollection<TogetherLayoutOption> Layouts { get; } = [];
    public ObservableCollection<TogetherDisplayOption> Displays { get; } = [];
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool CanStartSession => IsWindows && CanEditSession && Players.Count > 0;
    public bool CanEditSession => !IsStarting && !IsSessionRunning;
    public string ScreenDescription => IsWindows ? _localizer["together.display"] : _localizer["together.windowsOnly"];
    public string LayoutDescription => _localizer["together.layoutPreview"];
    public string LayoutLabel => _localizer["together.layout"];

    public void MovePlayerTo(TogetherPlayerViewModel source, int targetIndex)
    {
        if (!CanEditSession) return;
        var sourceIndex = Players.IndexOf(source);
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= Players.Count || sourceIndex == targetIndex) return;
        Players.Move(sourceIndex, targetIndex);
        for (var index = 0; index < Players.Count; index++) Players[index].Slot = index + 1;
        SavePlayers();
    }

    public TogetherViewModel(
        IProfileRepository profiles,
        IHytaleAuthenticator authenticator,
        IInstanceRepository instances,
        IGameLaunchCoordinator launchCoordinator,
        IGameProcessTracker processTracker,
        IExternalUriLauncher uriLauncher,
        StringLocalizer localizer,
        AppPathConfiguration appPath,
        bool discoverControllers = true)
    {
        _profiles = profiles;
        _authenticator = authenticator;
        _instances = instances;
        _launchCoordinator = launchCoordinator;
        _processTracker = processTracker;
        _uriLauncher = uriLauncher;
        _localizer = localizer;
        _appDir = appPath.AppDir;
        _localizer.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedChoices();
        RefreshDisplays();
        LoadSavedPlayers();
        RefreshChoices();
        if (discoverControllers) RefreshControllersCommand.Execute(null);
    }

    public string Title => _localizer["together.title"];
    public string Subtitle => _localizer["together.subtitle"];
    public string AddPlayerLabel => _localizer["together.addPlayer"];
    public string StartLabel => _localizer["together.start"];
    public string StopLabel => _localizer["together.stop"];
    public string RefreshControllersLabel => _localizer["together.refreshControllers"];
    public string ProfileLabel => _localizer["together.account"];
    public string InstanceLabel => _localizer["together.instance"];
    public string ControllerLabel => _localizer["together.controller"];
    public string TouchpadLabel => _localizer["together.touchpad"];
    public string VibrationLabel => _localizer["together.vibrationTest"];
    public string EmptyProfilesLabel => _localizer["together.noProfiles"];

    [RelayCommand(CanExecute = nameof(CanEditSession))]
    private void AddPlayer()
    {
        if (!CanEditSession || Players.Count >= 4) return;
        Players.Add(CreatePlayer());
        SavePlayers();
        OnPropertyChanged(nameof(CanStartSession));
    }

    [RelayCommand(CanExecute = nameof(CanEditSession))]
    private void RemovePlayer(TogetherPlayerViewModel? player)
    {
        if (player is null || !CanEditSession) return;
        Players.Remove(player);
        SavePlayers();
        OnPropertyChanged(nameof(CanStartSession));
    }

    [RelayCommand]
    private void RefreshChoices()
    {
        if (IsSessionRunning) return;
        var profileOptions = _profiles.GetProfiles()
            .Where(profile => profile.IsOfficial && Guid.TryParse(profile.UUID, out _))
            .Select(profile => new TogetherProfileOption(profile.Id, profile.Name, profile.UUID))
            .ToArray();
        var installedOptions = _instances.GetInstalledInstances()
            .Where(instance => instance.IsValid)
            .Select(instance => new TogetherInstanceOption(
                instance.Id,
                string.IsNullOrWhiteSpace(instance.CustomName) ? $"{instance.Branch} · {instance.Version}" : instance.CustomName,
                instance.Branch,
                instance.Path))
            .ToArray();

        foreach (var player in Players)
        {
            var profileId = player.SelectedProfile?.Id;
            var instanceId = player.SelectedInstance?.Id;
            player.SetChoices(profileOptions, installedOptions);
            player.SelectedProfile = player.Profiles.FirstOrDefault(option => option.Id == profileId)
                ?? player.Profiles.FirstOrDefault();
            player.SelectedInstance = player.Instances.FirstOrDefault(option => option.Id == instanceId)
                ?? player.Instances.FirstOrDefault();
        }
        SavePlayers();
    }

    [RelayCommand]
    private async Task RefreshControllersAsync()
    {
        Controllers.Clear();
        if (!IsWindows || !File.Exists(_nativeHostPath))
        {
            Status = _localizer["together.nativeMissing"];
            return;
        }

        try
        {
            var output = await RunNativeHostCheckedAsync(CancellationToken.None, "devices");

            foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split("  ", 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0].Length == 16 && parts[0].All(Uri.IsHexDigit))
                    Controllers.Add(new TogetherControllerOption(parts[0].ToLowerInvariant(), parts[1].Trim()));
            }

            foreach (var player in Players)
                player.SetControllers(Controllers.ToArray());
            Status = Controllers.Count == 0 ? _localizer["together.noControllers"] : string.Empty;
        }
        catch (Exception exception)
        {
            Status = $"{_localizer["together.controllerError"]} {RedactPath(exception.Message)}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartSession))]
    private async Task StartSessionAsync()
    {
        if (IsStarting || !IsWindows) return;
        if (!TryValidateSession(out var players, out var ownerIds, out var error))
        {
            Status = error;
            return;
        }

        IsStarting = true;
        StartSessionCommand.NotifyCanExecuteChanged();
        _sessionCancellation?.Dispose();
        _sessionCancellation = new CancellationTokenSource();
        var cancellationToken = _sessionCancellation.Token;
        try
        {
            for (var index = 0; index < players.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var player = players[index];
                var profile = _profiles.GetProfiles().First(item => item.Id == player.ProfileId);
                Status = string.Format(_localizer["together.launching"], player.Name, index + 1, players.Count);

                using (ProfileLaunchContext.Use(profile.Id))
                {
                    _authenticator.ReloadSessionForCurrentProfile();
                    var session = await _authenticator.EnsureFreshSessionForLaunchAsync();
                    if (session is null || session.AccountOwnerId != ownerIds[profile.Id])
                        throw new InvalidOperationException(string.Format(_localizer["together.accountExpired"], player.Name));

                    await _launchCoordinator.LaunchProfileAsync(
                        player.InstanceId,
                        profile.Id,
                        (uri, token) => _uriLauncher.LaunchAsync(uri, token));
                }

                var running = _processTracker.GetRunningProcesses()
                    .FirstOrDefault(process => process.InstanceId == player.InstanceId && process.ProfileId == player.ProfileId);
                if (running is null)
                    throw new InvalidOperationException(string.Format(_localizer["together.launchFailed"], player.Name));
                var window = await WaitForWindowAsync(running.ProcessId, cancellationToken);
                lock (_sessionGate)
                    _sessionWindows.Add(new PlayerWindow(running.ProcessId, window, GetWindowBounds(window), GetWindowLongPtr(window, -16)));
                await AttachControllerAsync(running.ProcessId, player, cancellationToken);
                await ApplyControllerColorAsync(player);
                SetWindowLongPtr(window, -16, new IntPtr(0x00CF0000));
                PlaceWindow(window, index, players.Count, Layout, SelectedDisplay?.Handle ?? IntPtr.Zero);
            }

            IsSessionRunning = true;
            Status = _localizer["together.sessionRunning"];
        }
        catch (OperationCanceledException)
        {
            await StopSessionAsync();
            Status = _localizer["together.cancelled"];
        }
        catch (Exception exception)
        {
            await StopSessionAsync();
            Status = $"{_localizer["together.startFailed"]} {RedactPath(exception.Message)}";
        }
        finally
        {
            IsStarting = false;
            StartSessionCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task StopSessionAsync()
    {
        _sessionCancellation?.Cancel();
        List<PlayerWindow> windows;
        lock (_sessionGate)
        {
            windows = [.. _sessionWindows];
            _sessionWindows.Clear();
        }

        foreach (var window in windows)
        {
            try
            {
                if (_processTracker.GetRunningProcesses().Any(process => process.ProcessId == window.ProcessId))
                    await RunNativeHostCheckedAsync(CancellationToken.None, "stop", window.ProcessId.ToString());
            }
            catch (Exception exception)
            {
                Status = $"{_localizer["together.stopWarning"]} {RedactPath(exception.Message)}";
            }
            finally
            {
                if (window.OriginalBounds is { } bounds)
                {
                    SetWindowLongPtr(window.Handle, -16, window.OriginalStyle);
                    SetWindowPos(window.Handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0020 | 0x0040);
                }
            }
        }

        IsSessionRunning = false;

        if (!IsStarting)
            Status = _localizer["together.sessionStopped"];
    }

    private bool TryValidateSession(
        out List<LaunchPlayer> selectedPlayers,
        out Dictionary<string, string> ownerIds,
        out string error)
    {
        selectedPlayers = [];
        ownerIds = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;

        if (Players.Count is < 1 or > 4)
        {
            error = _localizer["together.playerCountError"];
            return false;
        }

        foreach (var player in Players)
        {
            if (player.SelectedProfile is null || player.SelectedInstance is null)
            {
                error = _localizer["together.selectAll"];
                return false;
            }
            if (string.IsNullOrWhiteSpace(player.ControllerId))
            {
                error = _localizer["together.selectController"];
                return false;
            }
            if (ownerIds.ContainsKey(player.SelectedProfile.Id))
            {
                error = _localizer["together.duplicateProfile"];
                return false;
            }
            var ownerId = _authenticator.GetStoredAccountOwnerId(player.SelectedProfile.Id);
            if (string.IsNullOrWhiteSpace(ownerId))
            {
                error = string.Format(_localizer["together.accountExpired"], player.SelectedProfile.Name);
                return false;
            }
            ownerIds[player.SelectedProfile.Id] = ownerId;
            selectedPlayers.Add(new LaunchPlayer(
                player.SelectedProfile.Id,
                player.SelectedProfile.Name,
                player.SelectedInstance.Id,
                player.ControllerId,
                player.TouchpadMouseEnabled,
                player.EscapeButtonMode,
                player.Color));
        }

        if (Players.Select(player => player.ControllerId).Where(id => id != "kbm").Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != Players.Count(player => player.ControllerId != "kbm"))
        {
            error = _localizer["together.duplicateController"];
            return false;
        }
        if (Players.Count(player => player.ControllerId == "kbm") > 1)
        {
            error = _localizer["together.oneKeyboard"];
            return false;
        }
        if (ownerIds.Values.Distinct(StringComparer.Ordinal).Count() != ownerIds.Count)
        {
            error = _localizer["together.duplicateAccount"];
            return false;
        }
        if (Players.Select(player => player.SelectedInstance!.Branch).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            error = _localizer["together.sameBranch"];
            return false;
        }

        return true;
    }

    private async Task AttachControllerAsync(int processId, LaunchPlayer player, CancellationToken cancellationToken)
    {
        var touchpad = player.ControllerId == "kbm" ? "0" : player.TouchpadMouseEnabled ? "1" : "0";
        var escape = player.ControllerId == "kbm" ? "0" : player.EscapeButtonMode.ToString();
        await RunNativeHostCheckedAsync(cancellationToken, "attach", processId.ToString(), player.ControllerId, touchpad, escape);
    }

    private async Task ApplyControllerColorAsync(LaunchPlayer player)
    {
        if (player.ControllerId == "kbm") return;
        var color = ColorOptions.FirstOrDefault(item => item.Id == player.Color || item.Name == player.Color);
        if (color is null) return;
        try
        {
            if (!await _controllerFeatures.RunAsync(() => DualSenseManager.SetLedById(player.ControllerId, color.Red, color.Green, color.Blue)))
                Status = await _controllerFeatures.RunAsync(() => DualSenseManager.LastError);
        }
        catch (Exception exception)
        {
            Status = RedactPath(exception.Message);
        }
    }

    [RelayCommand]
    private async Task TestVibrationAsync(TogetherPlayerViewModel? player)
    {
        if (player is null || player.ControllerId == "kbm") return;
        try
        {
            if (!await _controllerFeatures.RunAsync(() => DualSenseManager.VibrateById(player.ControllerId, ushort.MaxValue, 250)))
                Status = await _controllerFeatures.RunAsync(() => DualSenseManager.LastError);
        }
        catch (Exception exception)
        {
            Status = RedactPath(exception.Message);
        }
    }

    private async Task<string> RunNativeHostCheckedAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        using var process = StartNativeHost(arguments);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            throw;
        }
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }

    private Process StartNativeHost(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(_nativeHostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(_nativeHostPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException(_localizer["together.nativeStartFailed"]);
    }

    private void LoadSavedPlayers()
    {
        try
        {
            var path = GetBindingsPath();
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<List<PlayerBinding>>(File.ReadAllText(path)) ?? [];
                foreach (var binding in saved.Take(4))
                    Players.Add(CreatePlayer(binding.ProfileId, binding.InstanceId, binding.ControllerId, binding.TouchpadMouseEnabled, binding.EscapeButtonMode, binding.Color));
            }
        }
        catch { }

        if (Players.Count == 0)
        {
            Players.Add(CreatePlayer());
            Players.Add(CreatePlayer());
        }
        SavePlayers();
    }

    private TogetherPlayerViewModel CreatePlayer(string? profileId = null, string? instanceId = null, string? controllerId = null,
        bool touchpadMouseEnabled = true, int escapeButtonMode = 1, string color = "electric-cyan")
    {
        var player = new TogetherPlayerViewModel(Players.Count + 1, RemovePlayerCommand, SavePlayers, _localizer);
        player.SetChoices(
            _profiles.GetProfiles().Where(profile => profile.IsOfficial && Guid.TryParse(profile.UUID, out _))
                .Select(profile => new TogetherProfileOption(profile.Id, profile.Name, profile.UUID)).ToArray(),
            _instances.GetInstalledInstances().Where(instance => instance.IsValid)
                .Select(instance => new TogetherInstanceOption(instance.Id,
                    string.IsNullOrWhiteSpace(instance.CustomName) ? $"{instance.Branch} · {instance.Version}" : instance.CustomName,
                    instance.Branch, instance.Path)).ToArray());
        player.SetControllers(Controllers.ToArray());
        player.SetControllerOptions(EscapeOptions, ColorOptions);
        player.SelectedProfile = player.Profiles.FirstOrDefault(item => item.Id == profileId)
            ?? player.Profiles.FirstOrDefault(item => Players.All(existing => existing.SelectedProfile?.Id != item.Id))
            ?? player.Profiles.FirstOrDefault();
        player.SelectedInstance = player.Instances.FirstOrDefault(item => item.Id == instanceId)
            ?? Players.Select(existing => existing.SelectedInstance).FirstOrDefault(item => item is not null)
            ?? player.Instances.FirstOrDefault();
        player.ControllerId = controllerId ?? (Players.Count == 0 ? "kbm" : Controllers.FirstOrDefault()?.Id ?? string.Empty);
        player.TouchpadMouseEnabled = touchpadMouseEnabled;
        player.EscapeButtonMode = escapeButtonMode;
        player.Color = ColorOptions.FirstOrDefault(option => option.Id == color || option.Name == color)?.Id ?? color;
        return player;
    }

    private void SavePlayers()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GetBindingsPath())!);
            File.WriteAllText(GetBindingsPath(), JsonSerializer.Serialize(
                Players.Select(player => new PlayerBinding(player.SelectedProfile?.Id, player.SelectedInstance?.Id,
                    player.ControllerId, player.TouchpadMouseEnabled, player.EscapeButtonMode, player.Color)).ToArray(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private string GetBindingsPath() => Path.Combine(_appDir, "Together", "players.json");

    private static string RedactPath(string value)
    {
        var username = Environment.UserName;
        return string.IsNullOrEmpty(username) ? value : value.Replace(username, "<user>", StringComparison.OrdinalIgnoreCase);
    }

    private static RECT? GetWindowBounds(IntPtr window)
    {
        try
        {
            return window != IntPtr.Zero && GetWindowRect(window, out var rect) ? rect : null;
        }
        catch { return null; }
    }

    private static async Task<IntPtr> WaitForWindowAsync(int processId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var window = IntPtr.Zero;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { window = Process.GetProcessById(processId).MainWindowHandle; }
            catch (ArgumentException) { break; }
            if (window != IntPtr.Zero) break;
            await Task.Delay(100, cancellationToken);
        }
        if (window == IntPtr.Zero) throw new InvalidOperationException($"Game window for PID {processId} did not become ready.");
        return window;
    }

    private static void PlaceWindow(IntPtr window, int index, int count, string layout, IntPtr monitor)
    {
        var workArea = GetMonitorWorkArea(monitor);

        int x, y, width, height;
        if (count == 1)
        {
            x = workArea.Left; y = workArea.Top; width = workArea.Width; height = workArea.Height;
        }
        else if (count == 2 && layout == "SideBySide")
        {
            width = workArea.Width / 2; height = workArea.Height; x = workArea.Left + width * index; y = workArea.Top;
        }
        else if (count == 2)
        {
            width = workArea.Width; height = workArea.Height / 2; x = workArea.Left; y = workArea.Top + height * index;
        }
        else if (count == 3 && layout == "Asymmetric")
        {
            width = index == 0 ? workArea.Width / 2 : workArea.Width / 4;
            height = index == 0 ? workArea.Height : workArea.Height / 2;
            x = workArea.Left + (index == 0 ? 0 : workArea.Width / 2);
            y = workArea.Top + (index == 2 ? workArea.Height / 2 : 0);
        }
        else
        {
            width = workArea.Width / 2; height = workArea.Height / 2;
            x = workArea.Left + width * (index % 2); y = workArea.Top + height * (index / 2);
        }

        if (!SetWindowPos(window, IntPtr.Zero, x, y, width, height, 0x0020 | 0x0040))
            throw new InvalidOperationException("Windows could not place a game window on the selected display.");
    }

    private void RefreshDisplays()
    {
        Displays.Clear();
        if (!IsWindows) return;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var bounds = info.WorkArea;
                var name = info.DeviceName.TrimEnd('\0');
                Displays.Add(new TogetherDisplayOption(monitor, $"{name} · {bounds.Width} × {bounds.Height}"));
            }
            return true;
        }, IntPtr.Zero);
        if (SelectedDisplay is null || !Displays.Contains(SelectedDisplay))
            SelectedDisplay = Displays.FirstOrDefault();
    }

    private static RECT GetMonitorWorkArea(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info)) return info.WorkArea;
        }
        var primary = new RECT();
        if (!SystemParametersInfo(0x0030, 0, ref primary, 0))
            throw new InvalidOperationException("Could not read the primary display work area.");
        return primary;
    }

    private void OnLanguageChanged(string _)
    {
        OnPropertyChanged(string.Empty);
        OnPropertyChanged(nameof(LayoutLabel));
        RefreshLocalizedChoices();
        foreach (var player in Players) player.RefreshLocalization();
    }

    private void RefreshLocalizedChoices()
    {
        EscapeOptions.Clear();
        EscapeOptions.Add(new(0, _localizer["together.escapeNone"]));
        EscapeOptions.Add(new(1, _localizer["together.escapeShare"]));
        EscapeOptions.Add(new(2, _localizer["together.escapePs"]));
        EscapeOptions.Add(new(3, _localizer["together.escapeBoth"]));
        ColorOptions.Clear();
        ColorOptions.Add(new("electric-cyan", _localizer["together.colorElectricCyan"], 0, 210, 255));
        ColorOptions.Add(new("warm-amber", _localizer["together.colorWarmAmber"], 245, 166, 35));
        ColorOptions.Add(new("neon-emerald", _localizer["together.colorNeonEmerald"], 16, 185, 129));
        ColorOptions.Add(new("royal-purple", _localizer["together.colorRoyalPurple"], 168, 85, 247));
        ColorOptions.Add(new("crimson-red", _localizer["together.colorCrimsonRed"], 239, 68, 68));
        ColorOptions.Add(new("golden-yellow", _localizer["together.colorGoldenYellow"], 234, 179, 8));
        ColorOptions.Add(new("hot-pink", _localizer["together.colorHotPink"], 236, 72, 153));
        ColorOptions.Add(new("pure-white", _localizer["together.colorPureWhite"], 255, 255, 255));
        ColorOptions.Add(new("off", _localizer["together.colorOff"], 0, 0, 0));
        Layouts.Clear();
        Layouts.Add(new("Grid", _localizer["together.layoutGrid"]));
        Layouts.Add(new("SideBySide", _localizer["together.layoutSideBySide"]));
        Layouts.Add(new("Asymmetric", _localizer["together.layoutAsymmetric"]));
        foreach (var player in Players) player.SetControllerOptions(EscapeOptions, ColorOptions);
    }

    public void Dispose()
    {
        _localizer.LanguageChanged -= OnLanguageChanged;
        try { StopSessionAsync().GetAwaiter().GetResult(); } catch { }
        _sessionCancellation?.Dispose();
        _controllerFeatures.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint flags);

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int Size;
        public RECT MonitorArea;
        public RECT WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    }

    private sealed record LaunchPlayer(string ProfileId, string Name, string InstanceId, string ControllerId,
        bool TouchpadMouseEnabled, int EscapeButtonMode, string Color);
    private sealed record PlayerBinding(string? ProfileId, string? InstanceId, string ControllerId,
        bool TouchpadMouseEnabled = true, int EscapeButtonMode = 1, string Color = "Electric Cyan");
    private sealed record PlayerWindow(int ProcessId, IntPtr Handle, RECT? OriginalBounds, IntPtr OriginalStyle);
}

public sealed partial class TogetherPlayerViewModel : ObservableObject
{
    private readonly IRelayCommand<TogetherPlayerViewModel?> _removeCommand;
    private readonly Action _save;
    private readonly StringLocalizer _localizer;

    [ObservableProperty]
    private TogetherProfileOption? _selectedProfile;

    [ObservableProperty]
    private TogetherInstanceOption? _selectedInstance;

    [ObservableProperty]
    private string _controllerId = string.Empty;

    [ObservableProperty]
    private bool _touchpadMouseEnabled = true;

    [ObservableProperty]
    private int _escapeButtonMode = 1;

    [ObservableProperty]
    private string _color = "electric-cyan";

    public TogetherPlayerViewModel(int slot, IRelayCommand<TogetherPlayerViewModel?> removeCommand, Action save, StringLocalizer localizer)
    {
        Slot = slot;
        _removeCommand = removeCommand;
        _save = save;
        _localizer = localizer;
    }

    [ObservableProperty]
    private int _slot;
    public ObservableCollection<TogetherProfileOption> Profiles { get; } = [];
    public ObservableCollection<TogetherInstanceOption> Instances { get; } = [];
    public ObservableCollection<TogetherControllerOption> Controllers { get; } = [];
    public ObservableCollection<TogetherEscapeOption> EscapeOptions { get; } = [];
    public ObservableCollection<TogetherColorOption> ColorOptions { get; } = [];
    public string RemoveLabel => _localizer["together.remove"];
    public string ProfileLabel => _localizer["together.account"];
    public string InstanceLabel => _localizer["together.instance"];
    public string ControllerLabel => _localizer["together.controller"];
    public string TouchpadLabel => _localizer["together.touchpad"];
    public string VibrationLabel => _localizer["together.vibrationTest"];
    public IRelayCommand<TogetherPlayerViewModel?> RemoveCommand => _removeCommand;

    public void SetChoices(IEnumerable<TogetherProfileOption> profiles, IEnumerable<TogetherInstanceOption> instances)
    {
        Profiles.Clear(); foreach (var profile in profiles) Profiles.Add(profile);
        Instances.Clear(); foreach (var instance in instances) Instances.Add(instance);
    }

    public void SetControllers(IEnumerable<TogetherControllerOption> controllers)
    {
        var id = ControllerId;
        Controllers.Clear();
        Controllers.Add(new TogetherControllerOption("kbm", "Keyboard & Mouse"));
        foreach (var controller in controllers) Controllers.Add(controller);
        ControllerId = Controllers.Any(item => item.Id == id)
            ? id == "kbm" && Slot > 1 && controllers.Any() ? controllers.First().Id : id
            : Slot == 1 ? "kbm" : controllers.FirstOrDefault()?.Id ?? "kbm";
    }

    public void SetControllerOptions(IEnumerable<TogetherEscapeOption> escapeOptions, IEnumerable<TogetherColorOption> colorOptions)
    {
        EscapeOptions.Clear(); foreach (var option in escapeOptions) EscapeOptions.Add(option);
        ColorOptions.Clear(); foreach (var option in colorOptions) ColorOptions.Add(option);
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(RemoveLabel));
        OnPropertyChanged(nameof(ProfileLabel));
        OnPropertyChanged(nameof(InstanceLabel));
        OnPropertyChanged(nameof(ControllerLabel));
        OnPropertyChanged(nameof(TouchpadLabel));
        OnPropertyChanged(nameof(VibrationLabel));
    }

    partial void OnSelectedProfileChanged(TogetherProfileOption? value) => _save();
    partial void OnSelectedInstanceChanged(TogetherInstanceOption? value) => _save();
    partial void OnControllerIdChanged(string value) => _save();
    partial void OnTouchpadMouseEnabledChanged(bool value) => _save();
    partial void OnEscapeButtonModeChanged(int value) => _save();
    partial void OnColorChanged(string value) => _save();
}

public sealed record TogetherProfileOption(string Id, string Name, string Uuid);
public sealed record TogetherInstanceOption(string Id, string Name, string Branch, string Path);
public sealed record TogetherControllerOption(string Id, string Name);
public sealed record TogetherEscapeOption(int Id, string Name);
public sealed record TogetherColorOption(string Id, string Name, byte Red, byte Green, byte Blue);
public sealed record TogetherLayoutOption(string Id, string Name);
public sealed record TogetherDisplayOption(IntPtr Handle, string Name);

internal sealed class ControllerFeatureRunner : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public ControllerFeatureRunner()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Hyprism controller features" };
        _thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try { completion.TrySetResult(operation()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task;
    }

    private void Run()
    {
        foreach (var operation in _work.GetConsumingEnumerable())
            operation();
        try { DualSenseManager.Shutdown(); } catch { }
    }

    public void Dispose()
    {
        if (_work.IsAddingCompleted) return;
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
        _work.Dispose();
    }
}

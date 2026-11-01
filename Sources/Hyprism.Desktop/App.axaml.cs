// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hyprism.Desktop.Integrations.GitHub;
using Hyprism.Desktop.Screens.News;
using Hyprism.Desktop.Screens.Settings;
using Hyprism.Desktop.Platform;
using Hyprism.Desktop.Localization;
using Hyprism.Desktop.Shell;
using Hyprism.Core;
using Hyprism.Core.Application.Ports;
using Hyprism.Core.Application.Progress;
using Hyprism.Core.Game;
using Hyprism.Core.Game.Instances;
using Hyprism.Core.Game.Launch;
using Hyprism.Core.Game.Mods;
using Hyprism.Core.Game.Sources;
using Hyprism.Core.Game.Versions;
using Hyprism.Core.Accounts;
using Hyprism.Core.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Hyprism.Desktop;

public sealed partial class App : Application
{
    private MainWindowViewModel? _mainWindowViewModel;
    private readonly CancellationTokenSource _bootstrapCancellation = new();
    private Task? _bootstrapTask;
    private string _startupPhase = "framework initialization";
    private bool _startupCompleted;
    private bool _uiFailureReported;

    public override void Initialize()
        => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Dispatcher.UIThread.UnhandledException += OnUiUnhandledException;
            var services = DesktopRuntime.Services;
            var settings = services.GetRequiredService<IDesktopSettingsStore>();
            DesktopTheme.Apply(settings.Theme);
            DesktopTheme.ApplyAccent(settings.AccentColor);

            var localizer = new StringLocalizer(settings.Language);
            if (!string.Equals(settings.Language, localizer.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
                settings.Language = localizer.CurrentLanguage;

            var mainWindow = new MainWindow();
            var uriLauncher = new ExternalUriLauncher(() => mainWindow);
            var filePicker = new FilePicker(() => mainWindow);
            mainWindow.DataContext = new StartupLoadingViewModel(localizer);
            desktop.MainWindow = mainWindow;

            desktop.Exit += OnDesktopExit;
            // Let Avalonia show the lightweight startup view before bootstrap
            // begins. InitializeAsync starts its blocking work on a worker.
            base.OnFrameworkInitializationCompleted();

            _bootstrapTask = InitializeAsync(
                services,
                mainWindow,
                settings,
                uriLauncher,
                filePicker,
                localizer,
                _bootstrapCancellation.Token);
            return;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _bootstrapCancellation.Cancel();
        _mainWindowViewModel?.Dispose();
        (DesktopRuntime.Services as IDisposable)?.Dispose();
        Logger.Shutdown();
    }

    private async Task InitializeAsync(
        IServiceProvider services,
        MainWindow mainWindow,
        IDesktopSettingsStore settings,
        IExternalUriLauncher uriLauncher,
        IFilePicker filePicker,
        StringLocalizer localizer,
        CancellationToken cancellationToken)
    {
        MainWindowViewModel? viewModel = null;
        try
        {
            _startupPhase = "background services";
            Logger.Info("Startup", $"Phase started: {_startupPhase}");
            await Task.Run(
                async () =>
                {
                    services.GetRequiredService<IDiscordPresence>().Initialize();
                    await InitializeCoreAsync(services, cancellationToken).ConfigureAwait(false);
                },
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            _startupPhase = "main window model construction";
            Logger.Info("Startup", $"Phase started: {_startupPhase}");
            var created = await Task.Run(
                () => CreateMainWindowViewModel(
                    services,
                    settings,
                    uriLauncher,
                    filePicker,
                    localizer),
                cancellationToken);

            _startupPhase = "main window UI handoff";
            Logger.Info("Startup", $"Phase started: {_startupPhase}");
            viewModel = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                created.BeginStartupLoading();
                mainWindow.DataContext = created;
                _mainWindowViewModel = created;
                created.BeginStartupHardwareDiscovery();
                return created;
            });

            _startupPhase = "startup data preload";
            Logger.Info("Startup", $"Phase started: {_startupPhase}");
            var minimumVisibleTime = Task.Delay(TimeSpan.FromMilliseconds(950), cancellationToken);
            await Task.WhenAll(
                PreloadDynamicContentAsync(viewModel, cancellationToken),
                minimumVisibleTime);

            _startupPhase = "final startup UI transition";
            Logger.Info("Startup", $"Phase started: {_startupPhase}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                viewModel.BeginOnboardingIfNeeded();
                viewModel.CompleteStartupLoading();
            });
            _startupCompleted = true;
            Logger.Info("Startup", "Startup completed successfully");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            var reportPath = StartupFailureReporter.Write(exception, _startupPhase);
            Logger.Error("Startup", $"Startup failed during {_startupPhase} ({exception.GetType().Name}). Diagnostic file: {reportPath}.");
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (viewModel is not null)
                        viewModel.ReportStartupFailure(reportPath);
                    else if (mainWindow.DataContext is StartupLoadingViewModel loading)
                        loading.ReportFailure(reportPath);
                });
            }
            catch (Exception reportingException)
            {
                StartupFailureReporter.Write(reportingException, "startup failure UI reporting");
            }
        }
    }

    private void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_uiFailureReported)
            return;

        _uiFailureReported = true;
        var reportPath = StartupFailureReporter.Write(e.Exception, _startupPhase);
        Logger.Error("Startup", $"Unhandled UI exception during {_startupPhase} ({e.Exception.GetType().Name}). Diagnostic file: {reportPath}.");
        if (_startupCompleted)
            return;

        e.Handled = true;
        try
        {
            if (_mainWindowViewModel is not null)
                _mainWindowViewModel.ReportStartupFailure(reportPath);
        }
        catch
        {
            e.Handled = false;
        }
    }

    private static MainWindowViewModel CreateMainWindowViewModel(
        IServiceProvider services,
        IDesktopSettingsStore settings,
        IExternalUriLauncher uriLauncher,
        IFilePicker filePicker,
        StringLocalizer localizer)
        => new(
            services.GetRequiredService<IInstanceRepository>(),
            services.GetRequiredService<IProfileManager>(),
            services.GetRequiredService<IProfileRepository>(),
            services.GetRequiredService<IGameLaunchCoordinator>(),
            services.GetRequiredService<IGameInstallationWorkflow>(),
            services.GetRequiredService<IGameProcessTracker>(),
            services.GetRequiredService<IProgressReporter>(),
            settings,
            services.GetRequiredService<IHytaleNewsClient>(),
            uriLauncher,
            services.GetRequiredService<HttpClient>(),
            localizer,
            filePicker,
            services.GetRequiredService<IGitHubClient>(),
            services.GetRequiredService<IMirrorCatalog>(),
            services.GetRequiredService<IMirrorDiscovery>(),
            services.GetRequiredService<IGameVersionCatalog>(),
            services.GetRequiredService<IModManager>(),
            services.GetRequiredService<IHytaleAuthenticator>(),
            services.GetRequiredService<RemoteImageCache>(),
            services.GetRequiredService<IGameConsoleService>(),
            services.GetRequiredService<IGpuProvider>(),
            services.GetRequiredService<LogSessionPaths>(),
            services.GetRequiredService<AppPathConfiguration>());

    private static async Task InitializeCoreAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        try
        {
            await Bootstrapper.InitializeAsync(services, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.Error("Bootstrapper", $"Asynchronous initialization failed: {exception}");
        }
    }

    private static async Task PreloadDynamicContentAsync(
        MainWindowViewModel viewModel,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            await viewModel.PreloadStartupDataAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Warning("Startup", "Dynamic content preload exceeded the startup time limit");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.Warning("Startup", $"Dynamic content preload failed: {exception.Message}");
        }
    }
}

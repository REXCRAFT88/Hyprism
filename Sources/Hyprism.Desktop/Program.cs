// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Skia;
using Avalonia.X11;
using Hyprism.Desktop.Integrations.GitHub;
using Hyprism.Desktop.Screens.News;
using Hyprism.Desktop.Screens.Settings;
using Hyprism.Desktop.Integrations.Discord;
using Hyprism.Desktop.Platform;
using Hyprism.Core;
using Hyprism.Core.Accounts;
using Hyprism.Core.Infrastructure;
using Hyprism.Core.Application.Ports;
using Hyprism.Core.Game.Launch;
using Hyprism.LocalNode;
using Microsoft.Extensions.DependencyInjection;

namespace Hyprism.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Logger.CaptureOriginalConsole();
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
                StartupFailureReporter.Write(exception, "unhandled process exception");
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            StartupFailureReporter.Write(eventArgs.Exception, "unobserved background task exception");
            eventArgs.SetObserved();
        };
        LauncherUserAgent.ConfigureVersion(DesktopApplicationInfo.Version);
        DesktopRuntime.Services = Bootstrapper.Initialize(services =>
        {
            services.AddSingleton<DesktopSettingsStore>();
            services.AddSingleton<IDesktopSettingsStore>(provider =>
                provider.GetRequiredService<DesktopSettingsStore>());
            services.AddSingleton<HytaleNewsClient>();
            services.AddSingleton<IHytaleNewsClient>(provider =>
                provider.GetRequiredService<HytaleNewsClient>());
            services.AddSingleton<GitHubClient>();
            services.AddSingleton<IGitHubClient>(provider =>
                provider.GetRequiredService<GitHubClient>());
            services.AddSingleton<RemoteImageCache>();
            services.AddSingleton<DiscordPresence>();
            services.AddSingleton<IDiscordPresence>(provider =>
                provider.GetRequiredService<DiscordPresence>());
            services.AddSingleton<GpuProvider>();
            services.AddSingleton<IGpuProvider>(provider =>
                provider.GetRequiredService<GpuProvider>());
            services.AddSingleton<LocalNodeServiceFactory>();
            services.AddSingleton<ILocalNodeServiceFactory>(provider =>
                provider.GetRequiredService<LocalNodeServiceFactory>());
            services.AddSingleton<IOAuthCallbackPageRenderer>(
                provider => new OAuthCallbackPageRenderer(
                    provider.GetRequiredService<IDesktopSettingsStore>()));
        });

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            var reportPath = StartupFailureReporter.Write(exception, "desktop lifetime startup");
            Logger.Error("Startup", $"Desktop lifetime failed ({exception.GetType().Name}). Diagnostic file: {reportPath}.");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#pragma warning disable AVALONIA_X11_CSD
            .With(new X11PlatformOptions
            {
                EnableDrawnDecorations = true
            })
#pragma warning restore AVALONIA_X11_CSD
            .With(DesktopRenderOptions.CreateWin32Options())
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 256 * 1024 * 1024
            });
}

internal static class DesktopRuntime
{
    public static IServiceProvider Services { get; set; } = null!;
}

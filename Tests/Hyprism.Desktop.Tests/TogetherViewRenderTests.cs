// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hyprism.Core;
using Hyprism.Core.Accounts;
using Hyprism.Desktop.Screens.Together;
using Hyprism.Desktop.Shell;
using Moq;
using Xunit;

namespace Hyprism.Desktop.Tests;

public sealed class TogetherViewRenderTests
{
    [AvaloniaFact]
    public async Task ProductionTogetherStartupKeepsShellAliveWhenControllerDiscoveryFails()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hyprism-controller-startup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var httpClient = new HttpClient();
        using var viewModel = await Task.Run(() => MainWindowViewModelFactory.Create(
            httpClient,
            authenticator: new Mock<IHytaleAuthenticator>().Object,
            appPath: new AppPathConfiguration(root),
            discoverTogetherControllers: true,
            controllerDiscovery: _ => Task.FromException<string>(new InvalidOperationException("fixture discovery failure"))));
        Assert.Null(viewModel.Together!.RefreshControllersCommand.ExecutionTask);
        var window = new MainWindow { DataContext = viewModel, Width = 900, Height = 600 };
        window.Show();
        viewModel.BeginStartupHardwareDiscovery();
        await viewModel.Together!.RefreshControllersCommand.ExecutionTask!;

        Assert.NotNull(viewModel.Together);
        Assert.Contains("fixture discovery failure", viewModel.Together!.Status, StringComparison.Ordinal);
        Assert.Empty(viewModel.Together.Controllers);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ControllerDiscoveryCompletingAfterShellBindingUpdatesCollectionsOnUiThread()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hyprism-controller-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var httpClient = new HttpClient();
        var discovery = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = await Task.Run(() => MainWindowViewModelFactory.Create(
            httpClient,
            authenticator: new Mock<IHytaleAuthenticator>().Object,
            appPath: new AppPathConfiguration(root),
            discoverTogetherControllers: true,
            controllerDiscovery: _ => discovery.Task));
        Assert.Null(viewModel.Together!.RefreshControllersCommand.ExecutionTask);
        var window = new MainWindow { DataContext = viewModel, Width = 900, Height = 600 };
        window.Show();
        var collectionChangesOnUiThread = true;
        var commandNotificationsOnUiThread = true;
        viewModel.Together!.Controllers.CollectionChanged += (_, _) =>
            collectionChangesOnUiThread &= Dispatcher.UIThread.CheckAccess();
        viewModel.Together.RefreshControllersCommand.CanExecuteChanged += (_, _) =>
            commandNotificationsOnUiThread &= Dispatcher.UIThread.CheckAccess();
        viewModel.BeginStartupHardwareDiscovery();
        discovery.SetResult($"0123456789abcdef  Fixture Pad{Environment.NewLine}");

        await viewModel.Together!.RefreshControllersCommand.ExecutionTask!;

        var controller = Assert.Single(viewModel.Together.Controllers);
        Assert.Equal("0123456789abcdef", controller.Id);
        Assert.Equal("Fixture Pad", controller.Name);
        Assert.True(collectionChangesOnUiThread);
        Assert.True(commandNotificationsOnUiThread);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TogetherViewRendersWithIsolatedSampleDataInEnglishAndRussian()
    {
        var dataRoot = Environment.GetEnvironmentVariable("HYPRISM_DATA")
            ?? Path.Combine(Path.GetTempPath(), $"hyprism-together-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);
        var screenshotRoot = Environment.GetEnvironmentVariable("HYPRISM_TOGETHER_SCREENSHOTS");

        foreach (var (language, folder) in new[] { ("en-US", "en"), ("ru-RU", "ru") })
        {
            using var httpClient = new HttpClient();
            var fixtureData = new AppPathConfiguration(Path.Combine(dataRoot!, folder));
            var bindingsPath = Path.Combine(fixtureData.AppDir, "Together", "players.json");
            Directory.CreateDirectory(Path.GetDirectoryName(bindingsPath)!);
            File.WriteAllText(bindingsPath, """
                [
                  { "ProfileId": "profile-hytale", "InstanceId": "instance-aurora", "ControllerId": "kbm", "TouchpadMouseEnabled": true, "EscapeButtonMode": 1, "Color": "electric-cyan" },
                  { "ProfileId": "profile-guest", "InstanceId": "instance-aurora", "ControllerId": "fixture-controller", "TouchpadMouseEnabled": true, "EscapeButtonMode": 1, "Color": "warm-amber" }
                ]
                """);
            using var viewModel = MainWindowViewModelFactory.Create(
                httpClient,
                language: language,
                authenticator: new Mock<IHytaleAuthenticator>().Object,
                appPath: fixtureData,
                discoverTogetherControllers: false,
                includeTogetherFixtureAccounts: true);
            viewModel.Together!.Controllers.Add(new TogetherControllerOption("fixture-controller", "USB Gamepad"));
            foreach (var player in viewModel.Together.Players)
                player.SetControllers(viewModel.Together.Controllers);
            var window = new MainWindow
            {
                Width = 1280,
                Height = 800,
                DataContext = viewModel
            };
            window.Show();
            viewModel.NavigateCommand.Execute("together");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();

            var togetherView = Assert.Single(window.GetVisualDescendants().OfType<TogetherView>());
            var preview = Assert.IsType<Canvas>(togetherView.FindControl<Canvas>("LayoutPreview"));
            Assert.Equal(2, preview.Children.Count);
            Assert.Equal(2, viewModel.Together!.Players.Count);
            Assert.Equal("Hytale Together", viewModel.Together.Title);

            if (!string.IsNullOrWhiteSpace(screenshotRoot))
            {
                var outputDirectory = Path.Combine(screenshotRoot, folder);
                Directory.CreateDirectory(outputDirectory);
                var path = Path.Combine(outputDirectory, "together.png");
                using var frame = new RenderTargetBitmap(
                    new PixelSize((int)window.Bounds.Width * 2, (int)window.Bounds.Height * 2),
                    new Vector(192, 192));
                frame.Render(window);
                frame.Save(path, PngBitmapEncoderOptions.Default);
            }

            window.Close();
        }
    }
}

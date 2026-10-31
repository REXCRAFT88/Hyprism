// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Hyprism.Core.Models;
using Hyprism.Core.Infrastructure;
using Hyprism.Core.Accounts;
using System.Net;
using System.Text.Json;

namespace Hyprism.Core.Tests.Accounts.Authentication;

public sealed class HytaleAuthenticatorTests
{
    [Fact]
    public async Task ExplicitProfileContext_LoadsTheMatchingSessionAfterAnotherProfileWasCached()
    {
        var appDir = Path.Combine(Path.GetTempPath(), $"hyprism-profile-auth-{Guid.NewGuid():N}");
        var profiles = new[]
        {
            new Profile { Id = Guid.NewGuid().ToString(), UUID = Guid.NewGuid().ToString(), Name = "Player A", IsOfficial = true },
            new Profile { Id = Guid.NewGuid().ToString(), UUID = Guid.NewGuid().ToString(), Name = "Player B", IsOfficial = true }
        };

        try
        {
            var profilesDir = LauncherUtilities.GetProfilesRoot(appDir);
            await File.WriteAllTextAsync(Path.Combine(profilesDir, "profiles.json"), JsonSerializer.Serialize(profiles));
            for (var i = 0; i < profiles.Length; i++)
            {
                var profileDir = LauncherUtilities.GetProfileFolderPath(appDir, profiles[i]);
                await File.WriteAllTextAsync(
                    Path.Combine(profileDir, "HytaleSession.json"),
                    JsonSerializer.Serialize(new HytaleAuthSession
                    {
                        AccessToken = $"token-{i}",
                        RefreshToken = $"refresh-{i}",
                        ExpiresAt = DateTime.UtcNow.AddHours(1),
                        UUID = profiles[i].UUID,
                        AccountOwnerId = $"owner-{i}"
                    }));
            }

            var config = new Mock<IConfigStore>();
            config.SetupGet(service => service.Configuration).Returns(new Config { SelectedProfileId = profiles[0].Id });
            using var httpClient = new HttpClient();
            var auth = new HytaleAuthenticator(httpClient, appDir, config.Object);

            using (ProfileLaunchContext.Use(profiles[0].Id))
            {
                auth.ReloadSessionForCurrentProfile();
                Assert.Equal("owner-0", auth.CurrentSession?.AccountOwnerId);
            }

            using (ProfileLaunchContext.Use(profiles[1].Id))
            {
                auth.ReloadSessionForCurrentProfile();
                await Task.Yield();
                Assert.Equal("owner-1", auth.CurrentSession?.AccountOwnerId);
                Assert.Equal("owner-1", auth.GetStoredAccountOwnerId(profiles[1].Id));
            }

            using (ProfileLaunchContext.Use(profiles[0].Id))
                Assert.Equal("owner-0", auth.CurrentSession?.AccountOwnerId);
        }
        finally
        {
            if (Directory.Exists(appDir)) Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public void SessionJson_WritesPascalCaseAndReadsLegacySnakeCase()
    {
        var session = new HytaleAuthSession
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc),
            SessionToken = "session",
            IdentityToken = "identity",
            Username = "Player",
            UUID = "550e8400-e29b-41d4-a716-446655440000",
            AccountOwnerId = "owner"
        };

        var json = JsonSerializer.Serialize(session);
        using var document = JsonDocument.Parse(json);
        Assert.All(
            document.RootElement.EnumerateObject(),
            property => Assert.True(char.IsUpper(property.Name[0]), property.Name));

        var restored = JsonSerializer.Deserialize<HytaleAuthSession>("""
            {
              "access_token": "legacy-access",
              "refresh_token": "legacy-refresh",
              "expires_at": "2026-08-24T12:00:00Z",
              "session_token": "legacy-session",
              "identity_token": "legacy-identity",
              "username": "LegacyPlayer",
              "uuid": "660e8400-e29b-41d4-a716-446655440000",
              "account_owner_id": "legacy-owner"
            }
            """);

        Assert.NotNull(restored);
        Assert.Equal("legacy-access", restored.AccessToken);
        Assert.Equal("legacy-refresh", restored.RefreshToken);
        Assert.Equal("legacy-session", restored.SessionToken);
        Assert.Equal("legacy-identity", restored.IdentityToken);
        Assert.Equal("LegacyPlayer", restored.Username);
        Assert.Equal("660e8400-e29b-41d4-a716-446655440000", restored.UUID);
        Assert.Equal("legacy-owner", restored.AccountOwnerId);
    }

    [Fact]
    public async Task LoginAsync_PresentsGeneratedAuthorizationUriThroughHostCallback()
    {
        var appDir = Path.Combine(Path.GetTempPath(), $"hyprism-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDir);

        try
        {
            var config = new Mock<IConfigStore>();
            config.SetupGet(service => service.Configuration).Returns(new Config());
            using var httpClient = new HttpClient();
            var auth = new HytaleAuthenticator(httpClient, appDir, config.Object);
            Uri? presentedUri = null;

            var session = await auth.LoginAsync((uri, _) =>
            {
                presentedUri = uri;
                return Task.FromResult(false);
            });

            Assert.Null(session);
            Assert.NotNull(presentedUri);
            Assert.Equal(Uri.UriSchemeHttps, presentedUri.Scheme);
            Assert.Equal("oauth.accounts.hytale.com", presentedUri.Host);
            Assert.Contains("code_challenge=", presentedUri.Query, StringComparison.Ordinal);
            Assert.Contains("state=", presentedUri.Query, StringComparison.Ordinal);
            Assert.Contains(
                "&scope=openid%20offline%20auth%3Alauncher&",
                presentedUri.AbsoluteUri,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoginAsync_ServesOfficialCallbackBeforePresentingAuthorizationUri()
    {
        var appDir = Path.Combine(Path.GetTempPath(), $"hyprism-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDir);

        try
        {
            var config = new Mock<IConfigStore>();
            config.SetupGet(service => service.Configuration).Returns(new Config());
            using var httpClient = new HttpClient();
            var auth = new HytaleAuthenticator(httpClient, appDir, config.Object);

            var session = await auth.LoginAsync(async (uri, cancellationToken) =>
            {
                var state = GetQueryValue(uri, "state");
                using var stateDocument = JsonDocument.Parse(Convert.FromBase64String(state));
                var port = stateDocument.RootElement.GetProperty("port").GetString();
                var callbackState = stateDocument.RootElement.GetProperty("state").GetString();
                Assert.False(string.IsNullOrWhiteSpace(port));
                Assert.False(string.IsNullOrWhiteSpace(callbackState));

                using var callbackClient = new HttpClient();
                using var response = await callbackClient.GetAsync(
                    $"http://127.0.0.1:{port}/authorization-callback" +
                    $"?error=access_denied&state={Uri.EscapeDataString(callbackState)}",
                    cancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
                return true;
            });

            Assert.Null(session);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoginAsync_UsesHostRendererForSuccessfulCallbackPage()
    {
        var appDir = Path.Combine(Path.GetTempPath(), $"hyprism-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDir);

        try
        {
            var config = new Mock<IConfigStore>();
            config.SetupGet(service => service.Configuration).Returns(new Config());
            var renderer = new Mock<IOAuthCallbackPageRenderer>();
            renderer
                .Setup(value => value.Render(true, It.IsAny<string>()))
                .Returns("<!doctype html><html><body>branded-success-page</body></html>");
            using var httpClient = new HttpClient();
            var auth = new HytaleAuthenticator(httpClient, appDir, config.Object, renderer.Object);

            var session = await auth.LoginAsync(async (uri, cancellationToken) =>
            {
                var state = GetQueryValue(uri, "state");
                using var stateDocument = JsonDocument.Parse(Convert.FromBase64String(state));
                var port = stateDocument.RootElement.GetProperty("port").GetString();
                var callbackState = stateDocument.RootElement.GetProperty("state").GetString();
                Assert.False(string.IsNullOrWhiteSpace(port));
                Assert.False(string.IsNullOrWhiteSpace(callbackState));

                using var callbackClient = new HttpClient();
                using var response = await callbackClient.GetAsync(
                    $"http://127.0.0.1:{port}/authorization-callback" +
                    $"?code=test-code&state={Uri.EscapeDataString(callbackState!)}",
                    cancellationToken);
                var responseHtml = await response.Content.ReadAsStringAsync(cancellationToken);

                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("branded-success-page", responseHtml, StringComparison.Ordinal);
                return false;
            });

            Assert.Null(session);
            renderer.Verify(
                value => value.Render(
                    true,
                    "Authorization completed successfully"),
                Times.Once);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoginAsync_DeliversCallbackPageBeforeCompletingAuthentication()
    {
        var appDir = Path.Combine(Path.GetTempPath(), $"hyprism-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDir);

        try
        {
            var config = new Mock<IConfigStore>();
            config.SetupGet(service => service.Configuration).Returns(new Config());
            var renderer = new Mock<IOAuthCallbackPageRenderer>();
            renderer
                .Setup(value => value.Render(true, It.IsAny<string>()))
                .Returns("<!doctype html><html><body>branded-success-page</body></html>");
            using var httpClient = new HttpClient(new SuccessfulOAuthHandler());
            var auth = new HytaleAuthenticator(httpClient, appDir, config.Object, renderer.Object);
            using var callbackClient = new HttpClient();
            Task<string>? callbackResponseTask = null;

            var session = await auth.LoginAsync((uri, _) =>
            {
                var state = GetQueryValue(uri, "state");
                using var stateDocument = JsonDocument.Parse(Convert.FromBase64String(state));
                var port = stateDocument.RootElement.GetProperty("port").GetString();
                var callbackState = stateDocument.RootElement.GetProperty("state").GetString();
                Assert.False(string.IsNullOrWhiteSpace(port));
                Assert.False(string.IsNullOrWhiteSpace(callbackState));

                callbackResponseTask = callbackClient.GetStringAsync(
                    $"http://127.0.0.1:{port}/authorization-callback" +
                    $"?code=test-code&state={Uri.EscapeDataString(callbackState!)}");
                return Task.FromResult(true);
            });

            Assert.NotNull(session);
            Assert.Equal("TestPlayer", session.Username);
            Assert.NotNull(callbackResponseTask);
            var responseHtml = await callbackResponseTask;

            Assert.Contains("branded-success-page", responseHtml, StringComparison.Ordinal);
            renderer.Verify(
                value => value.Render(
                    true,
                    "Authorization completed successfully"),
                Times.Once);
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    [Fact]
    public async Task LoginAsync_RejectsMissingHostCallback()
    {
        var appDir = Path.Combine(Path.GetTempPath(), $"hyprism-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDir);

        try
        {
            var config = new Mock<IConfigStore>();
            config.SetupGet(service => service.Configuration).Returns(new Config());
            using var httpClient = new HttpClient();
            var auth = new HytaleAuthenticator(httpClient, appDir, config.Object);

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => auth.LoginAsync(null!));
        }
        finally
        {
            Directory.Delete(appDir, recursive: true);
        }
    }

    private static string GetQueryValue(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator >= 0 ? pair[..separator] : pair;
            if (!string.Equals(Uri.UnescapeDataString(encodedName), name, StringComparison.Ordinal))
                continue;

            var encodedValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(encodedValue.Replace("+", " ", StringComparison.Ordinal));
        }

        throw new InvalidOperationException($"Query parameter '{name}' was not found");
    }

    private sealed class SuccessfulOAuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var responseBody = request.RequestUri?.Host switch
            {
                "oauth.accounts.hytale.com" => """
                    {"access_token":"access-token","refresh_token":"refresh-token","expires_in":3600}
                    """,
                "account-data.hytale.com" => """
                    {"owner":"owner","profiles":[{"username":"TestPlayer","uuid":"550e8400-e29b-41d4-a716-446655440000"}]}
                    """,
                "sessions.hytale.com" => """
                    {"sessionToken":"session-token","identityToken":"identity-token"}
                    """,
                "launcher.hytale.com" => """
                    {"version":"test"}
                    """,
                _ => throw new InvalidOperationException($"Unexpected HTTP request: {request.RequestUri}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody)
            });
        }
    }
}

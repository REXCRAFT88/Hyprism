// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Hyprism.Core.Accounts;

namespace Hyprism.Core.Tests.Accounts;

public sealed class ProfileLaunchContextTests
{
    [Fact]
    public void Use_RestoresPreviousContextWhenScopeExitsAfterFailure()
    {
        Assert.Null(ProfileLaunchContext.ProfileId);
        using (ProfileLaunchContext.Use("account-a"))
        {
            Assert.Equal("account-a", ProfileLaunchContext.ProfileId);
            Action failInsideScope = () =>
            {
                using var nested = ProfileLaunchContext.Use("account-b");
                Assert.Equal("account-b", ProfileLaunchContext.ProfileId);
                throw new InvalidOperationException("simulated launch failure");
            };
            Assert.Throws<InvalidOperationException>(failInsideScope);
            Assert.Equal("account-a", ProfileLaunchContext.ProfileId);
        }
        Assert.Null(ProfileLaunchContext.ProfileId);
    }

    [Fact]
    public async Task Use_KeepsParallelLaunchContextsIndependent()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;

        async Task<string?> ReadProfile(string profileId)
        {
            using var scope = ProfileLaunchContext.Use(profileId);
            if (Interlocked.Increment(ref arrivals) == 2) bothStarted.SetResult();
            await release.Task;
            return ProfileLaunchContext.ProfileId;
        }

        var first = Task.Run(() => ReadProfile("account-a"));
        var second = Task.Run(() => ReadProfile("account-b"));
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();

        Assert.Equal("account-a", await first);
        Assert.Equal("account-b", await second);
        Assert.Null(ProfileLaunchContext.ProfileId);
    }

    [Fact]
    public void GetAccountUserDataPath_MatchesLegacyTogetherAccountBranchLayout()
    {
        var accountA = ProfileLaunchContext.GetAccountUserDataPath("11111111-1111-1111-1111-111111111111", "release");
        var accountB = ProfileLaunchContext.GetAccountUserDataPath("22222222-2222-2222-2222-222222222222", "release");
        var preview = ProfileLaunchContext.GetAccountUserDataPath("11111111-1111-1111-1111-111111111111", "pre-release");

        Assert.EndsWith(Path.Combine("Hytale", "SplitScreen", "Accounts", "11111111-1111-1111-1111-111111111111", "release", "UserData"), accountA);
        Assert.NotEqual(accountA, accountB);
        Assert.NotEqual(accountA, preview);
        Assert.Throws<ArgumentException>(() => ProfileLaunchContext.GetAccountUserDataPath("not-a-uuid", "release"));
        Assert.Throws<ArgumentException>(() => ProfileLaunchContext.GetAccountUserDataPath("11111111-1111-1111-1111-111111111111", "other"));
    }

    [Fact]
    public void Use_RestoresIsolatedAccountDataFlag()
    {
        using (ProfileLaunchContext.Use("profile-a"))
        {
            Assert.False(ProfileLaunchContext.UsesIsolatedAccountData);
            using (ProfileLaunchContext.Use("profile-b", useIsolatedAccountData: true))
            {
                Assert.Equal("profile-b", ProfileLaunchContext.ProfileId);
                Assert.True(ProfileLaunchContext.UsesIsolatedAccountData);
            }
            Assert.Equal("profile-a", ProfileLaunchContext.ProfileId);
            Assert.False(ProfileLaunchContext.UsesIsolatedAccountData);
        }
        Assert.False(ProfileLaunchContext.UsesIsolatedAccountData);
    }
}

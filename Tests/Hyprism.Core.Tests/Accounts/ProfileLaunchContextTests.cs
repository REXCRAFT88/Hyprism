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
}

// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Hyprism.Desktop.Screens.Together;
using Xunit;

namespace Hyprism.Desktop.Tests;

public sealed class TogetherLayoutCalculatorTests
{
    [Fact]
    public void ThreePlayerAsymmetricLayoutUsesFullHeightPrimaryAndStackedHalfWidthTiles()
    {
        var first = TogetherLayoutCalculator.GetPreviewBounds(0, 3, "Asymmetric", 480, 260);
        var second = TogetherLayoutCalculator.GetPreviewBounds(1, 3, "Asymmetric", 480, 260);
        var third = TogetherLayoutCalculator.GetPreviewBounds(2, 3, "Asymmetric", 480, 260);

        Assert.Equal((0d, 0d, 240d, 260d), first);
        Assert.Equal((240d, 0d, 240d, 130d), second);
        Assert.Equal((240d, 130d, 240d, 130d), third);
    }

    [Fact]
    public void TwoPlayerSideBySideSplitsTheSelectedDisplayEvenly()
    {
        Assert.Equal((0d, 0d, 960d, 540d), TogetherLayoutCalculator.GetPreviewBounds(0, 2, "SideBySide", 1920, 540));
        Assert.Equal((960d, 0d, 960d, 540d), TogetherLayoutCalculator.GetPreviewBounds(1, 2, "SideBySide", 1920, 540));
    }

    [Fact]
    public void RejectsUnsupportedPlayerCountsAndIndexes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TogetherLayoutCalculator.GetPreviewBounds(0, 0, "Grid", 480, 260));
        Assert.Throws<ArgumentOutOfRangeException>(() => TogetherLayoutCalculator.GetPreviewBounds(2, 2, "Grid", 480, 260));
    }
}

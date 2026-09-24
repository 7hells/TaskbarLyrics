using Xunit;

namespace TaskbarLyrics.App.Tests;

public sealed class TaskbarFreeRegionCalculatorTests
{
    private const double TaskbarWidth = 1000;
    private const double DesiredWidth = 400;
    private const double MinimumWidth = 100;

    [Fact]
    public void CalculateWithNoObstructionsKeepsDesiredWidthAndAnchorsLeft()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth, [], LyricsHorizontalAnchor.Left, DesiredWidth, MinimumWidth);

        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(0, result.Left);
    }

    [Fact]
    public void CalculateWithNoObstructionsAnchorsRight()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth, [], LyricsHorizontalAnchor.Right, DesiredWidth, MinimumWidth);

        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(TaskbarWidth - DesiredWidth, result.Left);
    }

    [Fact]
    public void CalculateWithNoObstructionsCenters()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth, [], LyricsHorizontalAnchor.Center, DesiredWidth, MinimumWidth);

        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal((TaskbarWidth - DesiredWidth) / 2.0, result.Left);
    }

    [Fact]
    public void CalculateLeftAnchorShiftsPastStartButtonAndIcons()
    {
        // 开始按钮 [0,100] 与图标区 [100,300] 合并为 [0,300]；Left 锚点取其后空闲段。
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [new TaskbarObstruction(0, 100), new TaskbarObstruction(100, 300)],
            LyricsHorizontalAnchor.Left,
            DesiredWidth,
            MinimumWidth);

        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(300, result.Left);
    }

    [Fact]
    public void CalculateRightAnchorShiftsLeftOfNotificationArea()
    {
        // 通知区域 [900,1000]；Right 锚点取其左侧空闲段，右缘贴 900。
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [new TaskbarObstruction(900, 1000)],
            LyricsHorizontalAnchor.Right,
            DesiredWidth,
            MinimumWidth);

        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(500, result.Left);
    }

    [Fact]
    public void CalculateRightAnchorShrinksForWideTray()
    {
        // 托盘 [600,1000]，可用空间仅 600，期望 800 时收缩到 600。
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [new TaskbarObstruction(600, 1000)],
            LyricsHorizontalAnchor.Right,
            800,
            MinimumWidth);

        Assert.Equal(600, result.Width);
        Assert.Equal(0, result.Left);
    }

    [Fact]
    public void CalculateCenterAnchorCentersInFreeGap()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [new TaskbarObstruction(0, 100), new TaskbarObstruction(900, 1000)],
            LyricsHorizontalAnchor.Center,
            DesiredWidth,
            MinimumWidth);

        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(300, result.Left);
    }

    [Fact]
    public void CalculateCenterAnchorPicksLargestGapAmongMany()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [
                new TaskbarObstruction(0, 100),
                new TaskbarObstruction(300, 400),
                new TaskbarObstruction(900, 1000)
            ],
            LyricsHorizontalAnchor.Center,
            DesiredWidth,
            MinimumWidth);

        // 空闲段：100-300（宽 200）、400-900（宽 500），取后者居中。
        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(450, result.Left);
    }

    [Fact]
    public void CalculateNeverShrinksBelowMinimumWidth()
    {
        // 可用空间仅 50 < 最小宽度 100，保持最小宽度（接受部分遮挡）。
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [new TaskbarObstruction(50, 1000)],
            LyricsHorizontalAnchor.Left,
            DesiredWidth,
            MinimumWidth);

        Assert.Equal(MinimumWidth, result.Width);
    }

    [Fact]
    public void CalculateMergesOverlappingObstructions()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [
                new TaskbarObstruction(200, 500),
                new TaskbarObstruction(400, 800)
            ],
            LyricsHorizontalAnchor.Left,
            DesiredWidth,
            MinimumWidth);

        // 重叠合并为 [200,800]；Left 锚点最左空闲段 [0,200]。
        Assert.Equal(200, result.Width);
        Assert.Equal(0, result.Left);
    }

    [Fact]
    public void CalculateClampsOutOfBoundsObstructions()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [new TaskbarObstruction(-50, 250), new TaskbarObstruction(900, 1200)],
            LyricsHorizontalAnchor.Left,
            DesiredWidth,
            MinimumWidth);

        // 越界被 clamp 为 [0,250] 与 [900,1000]；Left 锚点取空闲段 [250,900]。
        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(250, result.Left);
    }

    [Fact]
    public void CalculateIgnoresEmptyOrInvertedObstructions()
    {
        var result = TaskbarFreeRegionCalculator.Calculate(
            TaskbarWidth,
            [new TaskbarObstruction(500, 500), new TaskbarObstruction(700, 300)],
            LyricsHorizontalAnchor.Left,
            DesiredWidth,
            MinimumWidth);

        Assert.Equal(DesiredWidth, result.Width);
        Assert.Equal(0, result.Left);
    }
}

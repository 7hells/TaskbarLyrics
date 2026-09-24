namespace TaskbarLyrics.App;

// 避让收缩后的窗口宽度与锚定左边缘（逻辑 DIP，未叠加 XOffset）。
internal readonly record struct TaskbarFreeRegionResult(double Width, double Left);

// 把任务栏上的系统占用区间换算成「不遮挡系统的可用空间」，并据此收缩窗口
// 宽度、保持锚点语义。纯逻辑，无原生依赖，可单元测试。
internal static class TaskbarFreeRegionCalculator
{
    public static TaskbarFreeRegionResult Calculate(
        double taskbarWidth,
        IReadOnlyList<TaskbarObstruction> obstructions,
        LyricsHorizontalAnchor anchor,
        double desiredWidth,
        double minimumWidth)
    {
        taskbarWidth = Math.Max(0, taskbarWidth);
        minimumWidth = Math.Max(0, minimumWidth);
        desiredWidth = Math.Max(minimumWidth, desiredWidth);

        var segments = MergeObstructions(obstructions, taskbarWidth);
        var available = ResolveAvailableRegion(segments, taskbarWidth, anchor);
        var width = Math.Clamp(available.Width, minimumWidth, desiredWidth);

        var left = anchor switch
        {
            LyricsHorizontalAnchor.Left => available.Left,
            LyricsHorizontalAnchor.Right => available.Right - width,
            _ => available.Left + ((available.Width - width) / 2.0)
        };
        left = Math.Clamp(left, 0, Math.Max(0, taskbarWidth - width));

        return new TaskbarFreeRegionResult(width, left);
    }

    // 把（可能重叠/相邻、可能越界的）占用区间合并成按左缘排序、不相交的段。
    private static List<TaskbarObstruction> MergeObstructions(
        IReadOnlyList<TaskbarObstruction> obstructions,
        double taskbarWidth)
    {
        var ordered = obstructions
            .Where(obstruction =>
                obstruction.Right > obstruction.Left &&
                obstruction.Right > 0 &&
                obstruction.Left < taskbarWidth)
            .OrderBy(obstruction => obstruction.Left)
            .ToList();
        if (ordered.Count == 0)
        {
            return [];
        }

        var merged = new List<TaskbarObstruction>();
        var currentLeft = Math.Max(0, ordered[0].Left);
        var currentRight = Math.Min(taskbarWidth, ordered[0].Right);
        foreach (var obstruction in ordered.Skip(1))
        {
            var left = Math.Max(0, obstruction.Left);
            var right = Math.Min(taskbarWidth, obstruction.Right);
            if (left <= currentRight)
            {
                currentRight = Math.Max(currentRight, right);
            }
            else
            {
                merged.Add(new TaskbarObstruction(currentLeft, currentRight));
                currentLeft = left;
                currentRight = right;
            }
        }

        merged.Add(new TaskbarObstruction(currentLeft, currentRight));
        return merged;
    }

    private static TaskbarObstruction ResolveAvailableRegion(
        IReadOnlyList<TaskbarObstruction> segments,
        double taskbarWidth,
        LyricsHorizontalAnchor anchor)
    {
        var gaps = CollectFreeGaps(segments, taskbarWidth);
        if (gaps.Count == 0)
        {
            return new TaskbarObstruction(0, 0);
        }

        return anchor switch
        {
            LyricsHorizontalAnchor.Left => gaps[0],
            LyricsHorizontalAnchor.Right => gaps[gaps.Count - 1],
            _ => gaps.OrderByDescending(gap => gap.Width).First()
        };
    }

    // 把合并后的占用段换算成它们之间的空闲段（含任务栏左右边缘）。
    private static List<TaskbarObstruction> CollectFreeGaps(
        IReadOnlyList<TaskbarObstruction> segments,
        double taskbarWidth)
    {
        if (segments.Count == 0)
        {
            return [new TaskbarObstruction(0, taskbarWidth)];
        }

        var gaps = new List<TaskbarObstruction>();
        var previousRight = 0.0;
        foreach (var segment in segments)
        {
            if (segment.Left > previousRight)
            {
                gaps.Add(new TaskbarObstruction(previousRight, segment.Left));
            }

            previousRight = Math.Max(previousRight, segment.Right);
        }

        if (previousRight < taskbarWidth)
        {
            gaps.Add(new TaskbarObstruction(previousRight, taskbarWidth));
        }

        return gaps;
    }
}

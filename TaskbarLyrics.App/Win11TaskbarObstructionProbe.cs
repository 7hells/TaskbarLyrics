using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace TaskbarLyrics.App;

// Windows 11 任务栏为 XAML 岛，经典 HWND 子窗口极少，改用 UI Automation
// 枚举任务栏元素并读取 BoundingRectangle 得到占用区间。
// 注意：UIA 树结构随 Windows 版本变化，且同步枚举可能耗时；探测失败或无法
// 定位任务栏时返回空集合，上层安全降级为「不收缩、沿用原宽度」。
internal sealed class Win11TaskbarObstructionProbe : ITaskbarObstructionProbe
{
    // 容器类元素（覆盖整段任务栏或作为布局骨架）不视为占用。
    private static readonly HashSet<ControlType> ContainerTypes = new()
    {
        ControlType.Pane,
        ControlType.Window,
        ControlType.ToolBar,
        ControlType.Custom,
        ControlType.List,
        ControlType.Group
    };

    public IReadOnlyList<TaskbarObstruction> Probe(TaskbarObstructionProbeContext context)
    {
        if (context.TaskbarHandle == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var root = AutomationElement.RootElement;
            var taskbar = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.NativeWindowHandleProperty,
                    context.TaskbarHandle.ToInt32()));
            if (taskbar == null)
            {
                return [];
            }

            var taskbarBounds = taskbar.Current.BoundingRectangle;
            var excluded = CollectLyricsWindowRuntimeIds(root, context.LyricsWindowHandle);
            var elements = taskbar.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            if (elements.Count == 0)
            {
                return [];
            }

            var obstructions = new List<TaskbarObstruction>();
            foreach (AutomationElement element in elements)
            {
                if (excluded.Contains(RuntimeIdKey(element.GetRuntimeId())))
                {
                    continue;
                }

                if (ContainerTypes.Contains(element.Current.ControlType))
                {
                    continue;
                }

                var rect = element.Current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }

                var left = Math.Max(0, (rect.Left - taskbarBounds.Left) / context.PixelsPerDip);
                var right = Math.Min(context.TaskbarWidth, (rect.Right - taskbarBounds.Left) / context.PixelsPerDip);
                if (right - left >= context.TaskbarWidth * 0.98)
                {
                    // 横跨整个任务栏的骨架元素（背景/容器）不视为占用。
                    continue;
                }

                if (right > left)
                {
                    obstructions.Add(new TaskbarObstruction(left, right));
                }
            }

            return obstructions;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return [];
        }
    }

    private static HashSet<string> CollectLyricsWindowRuntimeIds(
        AutomationElement root,
        IntPtr lyricsWindowHandle)
    {
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        if (lyricsWindowHandle == IntPtr.Zero)
        {
            return excluded;
        }

        var lyricsWindow = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(
                AutomationElement.NativeWindowHandleProperty,
                lyricsWindowHandle.ToInt32()));
        if (lyricsWindow == null)
        {
            return excluded;
        }

        foreach (AutomationElement element in lyricsWindow.FindAll(TreeScope.Subtree, Condition.TrueCondition))
        {
            excluded.Add(RuntimeIdKey(element.GetRuntimeId()));
        }

        return excluded;
    }

    private static string RuntimeIdKey(int[] runtimeId) =>
        string.Join(",", runtimeId);
}

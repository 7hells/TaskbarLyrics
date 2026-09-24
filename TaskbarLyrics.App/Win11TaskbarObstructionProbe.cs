using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace TaskbarLyrics.App;

// Windows 11 任务栏为 XAML 岛，系统元素（图标/托盘/时钟）通过 UI Automation
// 暴露的 BoundingRectangle 得到占用区间。
//
// 歌词窗口（WPF + WebView2）经 SetParent 嵌入后是任务栏的直接子窗口：探测时先
// 枚举任务栏的直接子元素，按 HWND 归属跳过歌词窗口整棵子树，既不误判也不触碰
// WebView2 内容（触碰会触发 Chromium accessibility 重建，歌词换行/封面更新时
// 表现为闪烁）。
//
// 系统元素按递归遍历收集。右键菜单（Menu/MenuItem 及菜单项文本）是挂在
// InputSite 下的临时弹出子树，遇到 Menu 即跳过整棵子树，避免右键任务栏时菜单
// 内容被误判为常驻占用。
//
// 探测失败或无法定位任务栏时返回空集合，上层安全降级为「不收缩、沿用原宽度」。
internal sealed class Win11TaskbarObstructionProbe : ITaskbarObstructionProbe
{
    // 容器类元素（覆盖整段任务栏或作为布局骨架）不视为占用，但仍递归其子元素。
    private static readonly HashSet<ControlType> ContainerTypes = new()
    {
        ControlType.Pane,
        ControlType.Window,
        ControlType.ToolBar,
        ControlType.Custom,
        ControlType.List,
        ControlType.Group
    };

    // 临时弹出/悬停元素（右键菜单、菜单项、工具提示）：整棵子树都不视为占用。
    private static readonly HashSet<ControlType> TransientTypes = new()
    {
        ControlType.Menu,
        ControlType.MenuBar,
        ControlType.MenuItem,
        ControlType.ToolTip
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
            var lyricsWindowHandles = EnumerateLyricsWindowHandles(context.LyricsWindowHandle);

            var obstructions = new List<TaskbarObstruction>();

            var children = taskbar.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement child in children)
            {
                if (IsLyricsWindowSubtree(child, lyricsWindowHandles))
                {
                    continue;
                }

                CollectObstructions(child, taskbarBounds, context, obstructions);
            }

            return obstructions;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return [];
        }
    }

    private static void CollectObstructions(
        AutomationElement element,
        System.Windows.Rect taskbarBounds,
        TaskbarObstructionProbeContext context,
        List<TaskbarObstruction> obstructions)
    {
        try
        {
            var controlType = element.Current.ControlType;

            // 右键菜单等临时弹出子树：整棵跳过（含菜单项文本）。
            if (TransientTypes.Contains(controlType))
            {
                return;
            }

            if (!ContainerTypes.Contains(controlType))
            {
                var rect = element.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                {
                    var left = Math.Max(0, (rect.Left - taskbarBounds.Left) / context.PixelsPerDip);
                    var right = Math.Min(context.TaskbarWidth, (rect.Right - taskbarBounds.Left) / context.PixelsPerDip);
                    if (right - left < context.TaskbarWidth * 0.98 && right > left)
                    {
                        obstructions.Add(new TaskbarObstruction(left, right));
                    }
                }
            }

            var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement child in children)
            {
                CollectObstructions(child, taskbarBounds, context, obstructions);
            }
        }
        catch (ElementNotAvailableException)
        {
            // 元素在遍历途中失效（如右键菜单刚关闭）：跳过该子树，不影响其他元素。
        }
    }

    private static bool IsLyricsWindowSubtree(AutomationElement element, HashSet<IntPtr> lyricsWindowHandles)
    {
        if (lyricsWindowHandles.Count == 0)
        {
            return false;
        }

        try
        {
            var nativeHandle = element.Current.NativeWindowHandle;
            return nativeHandle != 0 && lyricsWindowHandles.Contains(new IntPtr(nativeHandle));
        }
        catch (ElementNotAvailableException)
        {
            // 元素失效：归属不可靠，按歌词子树跳过以免误判为占用。
            return true;
        }
    }

    private static HashSet<IntPtr> EnumerateLyricsWindowHandles(IntPtr lyricsWindowHandle)
    {
        var handles = new HashSet<IntPtr>();
        if (lyricsWindowHandle == IntPtr.Zero)
        {
            return handles;
        }

        handles.Add(lyricsWindowHandle);
        _ = EnumChildWindows(lyricsWindowHandle, (child, _) =>
        {
            handles.Add(child);
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
}

using System.Runtime.InteropServices;

namespace TaskbarLyrics.App;

// Windows 10 经典任务栏通过 Shell_TrayWnd 的子窗口承载系统元素。用
// FindWindowEx 枚举这些子窗口，识别开始按钮、应用图标区与通知区域。
// 搜索框（Cortana）在新版 Win10 为 XAML 岛，HWND class 不稳定，按片段尽力匹配。
internal sealed class Win10TaskbarObstructionProbe : ITaskbarObstructionProbe
{
    private static readonly HashSet<string> KnownClasses = new(StringComparer.Ordinal)
    {
        "Start",             // 开始按钮
        "TrayNotifyWnd",     // 通知区域（托盘 + 时钟）
        "MSTaskSwWClass",    // 应用图标区
        "MSTaskListWClass"   // 应用列表
    };

    private static readonly string[] SearchBoxFragments =
    [
        "Cortana",
        "Search",
        "Windows.UI.Core.CoreWindow"
    ];

    public IReadOnlyList<TaskbarObstruction> Probe(TaskbarObstructionProbeContext context)
    {
        if (context.TaskbarHandle == IntPtr.Zero ||
            !GetWindowRect(context.TaskbarHandle, out var taskbarRect))
        {
            return [];
        }

        var obstructions = new List<TaskbarObstruction>();
        Enumerate(context.TaskbarHandle, taskbarRect, context, obstructions, depth: 0);
        return obstructions;
    }

    private static void Enumerate(
        IntPtr parent,
        TaskbarNativeMethods.NativeRect taskbarRect,
        TaskbarObstructionProbeContext context,
        List<TaskbarObstruction> obstructions,
        int depth)
    {
        // 任务栏窗口层级很浅；限制深度避免意外遍历过深。
        if (depth > 4)
        {
            return;
        }

        var child = IntPtr.Zero;
        while ((child = FindWindowEx(parent, child, null, null)) != IntPtr.Zero)
        {
            if (GetWindowRect(child, out var rect) && rect.Right > rect.Left)
            {
                var className = ReadClassName(child);
                if (IsObstructionClass(className))
                {
                    var left = Math.Max(0, (rect.Left - taskbarRect.Left) / context.PixelsPerDip);
                    var right = Math.Min(context.TaskbarWidth, (rect.Right - taskbarRect.Left) / context.PixelsPerDip);
                    if (right > left)
                    {
                        obstructions.Add(new TaskbarObstruction(left, right));
                    }
                }
            }

            Enumerate(child, taskbarRect, context, obstructions, depth + 1);
        }
    }

    private static bool IsObstructionClass(string? className)
    {
        if (string.IsNullOrEmpty(className))
        {
            return false;
        }

        return KnownClasses.Contains(className) ||
            SearchBoxFragments.Any(fragment =>
                className.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        IntPtr hwnd,
        out TaskbarNativeMethods.NativeRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, char[] className, int maxCount);
}

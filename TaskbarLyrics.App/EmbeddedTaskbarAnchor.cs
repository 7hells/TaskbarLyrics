using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

internal readonly record struct EmbeddedTaskbarNativeBounds(int Left, int Top, int Width, int Height);

internal readonly record struct EmbeddedTaskbarDisplayTarget(bool UsesPrimaryTaskbar, string? Id)
{
    public static EmbeddedTaskbarDisplayTarget Create(DisplayMonitor? targetDisplay) =>
        new(targetDisplay is null, targetDisplay?.Id);

    public bool Matches(DisplayMonitor? targetDisplay) =>
        UsesPrimaryTaskbar == (targetDisplay is null) &&
        string.Equals(Id, targetDisplay?.Id, StringComparison.OrdinalIgnoreCase);
}

internal enum EmbeddedTaskbarAttachResult
{
    Unavailable,
    Attached,
    AttachedPositionPending
}

internal static class EmbeddedTaskbarEmbeddingPolicy
{
    public static EmbeddedTaskbarAttachResult FromPositionResult(bool positioned) =>
        positioned
            ? EmbeddedTaskbarAttachResult.Attached
            : EmbeddedTaskbarAttachResult.AttachedPositionPending;

    public static bool ShouldKeepEmbedded(EmbeddedTaskbarAttachResult result) =>
        result is EmbeddedTaskbarAttachResult.Attached or EmbeddedTaskbarAttachResult.AttachedPositionPending;

    public static bool ShouldKeepExistingAttachment(
        bool sameWindow,
        bool sameTargetDisplay,
        bool parentIsValid) =>
        sameWindow && sameTargetDisplay && parentIsValid;

    // A re-parent of an established embedding to another taskbar cannot revive the
    // invalidated DWM composition surface of the same HWND (verified with native
    // probes), so the host must replace the window instead of re-parenting in place.
    public static bool RequiresWindowReplacement(
        EmbeddedTaskbarDisplayTarget? attachedTarget,
        DisplayMonitor? targetDisplay) =>
        attachedTarget is { } attached && !attached.Matches(targetDisplay);
}

internal static class EmbeddedTaskbarLayoutCalculator
{
    public static long CalculateIntersectionArea(NativeRect first, NativeRect second)
    {
        var width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        var height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return (long)width * height;
    }

    public static double CalculateHorizontalLeft(
        double taskbarWidth,
        double windowWidth,
        LyricsHorizontalAnchor anchor,
        double offset)
    {
        return anchor switch
        {
            LyricsHorizontalAnchor.Left => offset,
            LyricsHorizontalAnchor.Center => (taskbarWidth - windowWidth) / 2.0 + offset,
            _ => taskbarWidth - windowWidth + offset
        };
    }

    public static double CalculateVerticalTop(double taskbarHeight, double windowHeight, double offset)
    {
        return (taskbarHeight - windowHeight) / 2.0 + offset;
    }

    public static double ClampHorizontalLeft(
        double left,
        double taskbarWidth,
        double windowWidth)
    {
        var maximumLeft = Math.Max(0, taskbarWidth - windowWidth);
        return Math.Clamp(left, 0, maximumLeft);
    }

    public static double ClampVerticalTop(
        double top,
        double taskbarHeight,
        double windowHeight)
    {
        var maximumTop = Math.Max(0, taskbarHeight - windowHeight);
        return Math.Clamp(top, 0, maximumTop);
    }

    public static EmbeddedTaskbarNativeBounds ToTaskbarClientBounds(
        double clientLeft,
        double clientTop,
        double width,
        double height,
        double pixelsPerDip)
    {
        pixelsPerDip = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        return new EmbeddedTaskbarNativeBounds(
            AlignPhysicalPixel(clientLeft, pixelsPerDip),
            AlignPhysicalPixel(clientTop, pixelsPerDip),
            Math.Max(1, AlignPhysicalPixel(width, pixelsPerDip)),
            Math.Max(1, AlignPhysicalPixel(height, pixelsPerDip)));
    }

    public static bool NeedsNativeBoundsUpdate(
        EmbeddedTaskbarNativeBounds? previousBounds,
        EmbeddedTaskbarNativeBounds targetBounds) =>
        previousBounds is null || previousBounds.Value != targetBounds;

    private static int AlignPhysicalPixel(double value, double pixelsPerDip) =>
        checked((int)Math.Round(value * pixelsPerDip, MidpointRounding.AwayFromZero));
}

internal sealed class EmbeddedTaskbarAnchor : IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GetAncestorParent = 1;
    private const long WsPopup = 0x80000000L;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const int CoInitMultiThreaded = 0x0;
    private const int S_OK = 0;
    private const int S_FALSE = 1;

    private IntPtr _windowHandle;
    private IntPtr _taskbarHandle;
    private IntPtr _parentHandle;
    private IntPtr _taskListHandle;
    private EmbeddedTaskbarNativeBounds _originalTaskListBounds;
    private EmbeddedTaskbarNativeBounds? _lastTaskListBounds;
    private EmbeddedTaskbarDisplayTarget? _attachedDisplayTarget;
    private long _originalStyle;
    private long _originalExtendedStyle;
    private bool _hasOriginalStyles;
    private bool _taskListSqueezed;
    private bool _disposed;
    private string? _lastAttachDiagnostic;
    private readonly ITaskbarObstructionProbe _obstructionProbe = CreateObstructionProbe();
    private volatile IReadOnlyList<TaskbarObstruction> _cachedObstructions = [];
    private int _probeGeneration;
    private int _probeInFlight;
    private long _lastProbeTimestampTicks;
    private static readonly long ObstructionProbeMinIntervalTicks =
        (long)TimeSpan.FromSeconds(2).TotalMilliseconds;

    public bool IsAttached => _windowHandle != IntPtr.Zero && _parentHandle != IntPtr.Zero;

    public bool IsAttachedToDifferentTarget(DisplayMonitor? targetDisplay) =>
        EmbeddedTaskbarEmbeddingPolicy.RequiresWindowReplacement(_attachedDisplayTarget, targetDisplay);

    public EmbeddedTaskbarAttachResult Attach(Window window, AppSettings settings, DisplayMonitor? targetDisplay)
    {
        if (_disposed)
        {
            return EmbeddedTaskbarAttachResult.Unavailable;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            ReportAttachOutcome(
                EmbeddedTaskbarAttachResult.Unavailable,
                $"NoWindowHandle Target={DescribeTarget(targetDisplay)}");
            return EmbeddedTaskbarAttachResult.Unavailable;
        }

        // ForceAlwaysOnTop is a floating-window preference. A taskbar child must
        // never retain a stale topmost state after switching from floating mode.
        window.Topmost = false;

        if (HasAttachmentForDifferentTarget(hwnd, targetDisplay))
        {
            Detach();
        }

        var taskbar = FindTaskbar(targetDisplay, out var taskbarDiagnostic);
        if (taskbar == IntPtr.Zero)
        {
            var keptAttachment = KeepExistingAttachmentIfValid(window, hwnd, settings, targetDisplay);
            ReportAttachOutcome(keptAttachment, $"NoTaskbarForTarget {taskbarDiagnostic}");
            return keptAttachment;
        }

        if (_windowHandle != hwnd || _taskbarHandle != taskbar)
        {
            Detach();
            _windowHandle = hwnd;
            _taskbarHandle = taskbar;
            CaptureOriginalStyles(hwnd);
        }

        var isWindows11 = IsWindows11();
        var parent = isWindows11 ? taskbar : FindTaskbarContainer(taskbar);
        if (parent == IntPtr.Zero)
        {
            var keptAttachment = KeepExistingAttachmentIfValid(window, hwnd, settings, targetDisplay);
            ReportAttachOutcome(keptAttachment, "NoTaskbarContainer");
            return keptAttachment;
        }

        if (_parentHandle != parent || GetAncestor(hwnd, GetAncestorParent) != parent)
        {
            RestoreTaskList();
            _taskListHandle = IntPtr.Zero;
            _originalTaskListBounds = default;

            _ = SetParent(hwnd, parent);
            ApplyChildWindowStyle(hwnd);
            if (GetAncestor(hwnd, GetAncestorParent) != parent)
            {
                ReportAttachOutcome(EmbeddedTaskbarAttachResult.Unavailable, "ReparentRejected");
                return EmbeddedTaskbarAttachResult.Unavailable;
            }

            _parentHandle = parent;
        }

        _attachedDisplayTarget = EmbeddedTaskbarDisplayTarget.Create(targetDisplay);

        var attachResult = EmbeddedTaskbarEmbeddingPolicy.FromPositionResult(
            Position(hwnd, parent, taskbar, window, settings, targetDisplay));
        ScheduleObstructionProbe(hwnd, parent, taskbar, window, settings, targetDisplay);
        ReportAttachOutcome(attachResult, $"Positioned Target={DescribeTarget(targetDisplay)}");
        return attachResult;
    }

    public void Detach()
    {
        RestoreTaskList();

        if (_windowHandle != IntPtr.Zero && IsWindow(_windowHandle))
        {
            _ = SetParent(_windowHandle, IntPtr.Zero);
            RestoreTopLevelStyles(_windowHandle);
        }

        _windowHandle = IntPtr.Zero;
        _taskbarHandle = IntPtr.Zero;
        _parentHandle = IntPtr.Zero;
        _taskListHandle = IntPtr.Zero;
        _originalTaskListBounds = default;
        _lastTaskListBounds = null;
        _attachedDisplayTarget = null;
        _hasOriginalStyles = false;
        _taskListSqueezed = false;
        _cachedObstructions = [];
        Interlocked.Increment(ref _probeGeneration);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Detach();
        _disposed = true;
    }

    private bool Position(
        IntPtr hwnd,
        IntPtr parent,
        IntPtr taskbarHandle,
        Window window,
        AppSettings settings,
        DisplayMonitor? targetDisplay)
    {
        if (!GetWindowRect(parent, out var parentRect))
        {
            return false;
        }

        var pixelsPerDip = targetDisplay?.PixelsPerDip ?? TaskbarPlacementService.GetPixelsPerDip(window);
        var taskbarWidth = (parentRect.Right - parentRect.Left) / pixelsPerDip;
        var taskbarHeight = (parentRect.Bottom - parentRect.Top) / pixelsPerDip;
        var desiredWidth = AppSettings.ClampEffectiveWindowWidth(
            settings.WindowWidth,
            settings.LyricsLayoutScalePercent,
            taskbarWidth);
        var freeRegion = ResolveFreeRegion(taskbarWidth, settings, desiredWidth);
        var width = freeRegion.Width;
        var height = LyricsLayoutMetrics.Create(settings, pixelsPerDip).DesiredWindowHeight;
        height = Math.Min(height, taskbarHeight);

        if (window.Width != width)
        {
            window.Width = width;
        }

        if (window.Height != height)
        {
            window.Height = height;
        }

        var clientLeft = freeRegion.Left + settings.XOffset;
        var clientTop = EmbeddedTaskbarLayoutCalculator.CalculateVerticalTop(
            taskbarHeight,
            height,
            settings.YOffset);

        clientLeft = EmbeddedTaskbarLayoutCalculator.ClampHorizontalLeft(
            clientLeft,
            taskbarWidth,
            width);
        clientTop = EmbeddedTaskbarLayoutCalculator.ClampVerticalTop(
            clientTop,
            taskbarHeight,
            height);

        var bounds = EmbeddedTaskbarLayoutCalculator.ToTaskbarClientBounds(
            clientLeft,
            clientTop,
            width,
            height,
            pixelsPerDip);

        // WPF Window.Left/Top enforce top-level screen semantics: after any native
        // move WPF re-syncs them from the child's screen rectangle, and writing them
        // back applies the mixed-in screen coordinates as parent-client coordinates.
        // That feedback breaks every taskbar whose screen origin is not (0, 0), so
        // the embedded position is owned exclusively by the native SetWindowPos below
        // and is verified against the live window rectangle: external moves (WPF
        // size applies, DPI changes) are corrected on the next anchor pass.
        if (!EmbeddedTaskbarLayoutCalculator.NeedsNativeBoundsUpdate(
                GetCurrentWindowBounds(hwnd, parentRect),
                bounds))
        {
            return true;
        }

        return TaskbarNativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            TaskbarNativeMethods.SWP_NOZORDER | TaskbarNativeMethods.SWP_NOACTIVATE);
    }

    // 用最近一次后台探测到的占用区间计算收缩后的宽度与位置。探测尚未完成或
    // 失败时 _cachedObstructions 为空，安全降级为「不收缩、沿用原宽度」。
    private TaskbarFreeRegionResult ResolveFreeRegion(
        double taskbarWidth,
        AppSettings settings,
        double desiredWidth)
    {
        return TaskbarFreeRegionCalculator.Calculate(
            taskbarWidth,
            _cachedObstructions,
            settings.HorizontalAnchor,
            desiredWidth,
            AppSettings.MinimumWindowWidth);
    }

    // 在后台线程探测任务栏占用区，完成后回发到窗口 Dispatcher 更新缓存并重定位，
    // 避免 Win11 UI Automation 跨进程调用阻塞 UI 线程（曾导致任务栏卡死）。
    private void ScheduleObstructionProbe(
        IntPtr hwnd,
        IntPtr parent,
        IntPtr taskbarHandle,
        Window window,
        AppSettings settings,
        DisplayMonitor? targetDisplay)
    {
        // 节流：探测是昂贵的跨进程 UIA 枚举，而 60ms 主定时器每 tick 都会经
        // AnchorToTaskbar -> Attach 走到这里。不限制频率会持续堆积未释放的
        // AutomationElement COM 对象与线程池任务（长时间运行内存泄露、系统卡顿）。
        if (Interlocked.CompareExchange(ref _probeInFlight, 1, 0) != 0)
        {
            return;
        }

        var nowTicks = Environment.TickCount64;
        if (nowTicks - Volatile.Read(ref _lastProbeTimestampTicks) < ObstructionProbeMinIntervalTicks)
        {
            Interlocked.Exchange(ref _probeInFlight, 0);
            return;
        }

        Volatile.Write(ref _lastProbeTimestampTicks, nowTicks);

        if (!GetWindowRect(parent, out var parentRect))
        {
            Interlocked.Exchange(ref _probeInFlight, 0);
            return;
        }

        var pixelsPerDip = targetDisplay?.PixelsPerDip ?? TaskbarPlacementService.GetPixelsPerDip(window);
        var context = new TaskbarObstructionProbeContext(
            taskbarHandle,
            parent,
            hwnd,
            pixelsPerDip,
            (parentRect.Right - parentRect.Left) / pixelsPerDip,
            (parentRect.Bottom - parentRect.Top) / pixelsPerDip);
        var dispatcher = window.Dispatcher;
        var generation = ++_probeGeneration;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var obstructions = ProbeInComContext(context);
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    return;
                }

                dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || generation != _probeGeneration)
                    {
                        return;
                    }

                    if (AreSameObstructions(_cachedObstructions, obstructions))
                    {
                        return;
                    }

                    _cachedObstructions = obstructions;
                    Log.Diagnostic(
                        "EMBED",
                        $"ObstructionsChanged Count={obstructions.Count} " +
                        string.Join(" ", obstructions.Select(obstruction => $"[{obstruction.Left:0.#},{obstruction.Right:0.#}]")));
                    Position(hwnd, parent, taskbarHandle, window, settings, targetDisplay);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _probeInFlight, 0);
            }
        });
    }

    private IReadOnlyList<TaskbarObstruction> ProbeInComContext(TaskbarObstructionProbeContext context)
    {
        // Win11 UI Automation 需要 COM；后台线程统一用 MTA 初始化。
        var initializeResult = CoInitializeEx(IntPtr.Zero, CoInitMultiThreaded);
        try
        {
            return _obstructionProbe.Probe(context);
        }
        finally
        {
            if (initializeResult is S_OK or S_FALSE)
            {
                CoUninitialize();
            }
        }
    }

    private static bool AreSameObstructions(
        IReadOnlyList<TaskbarObstruction>? first,
        IReadOnlyList<TaskbarObstruction>? second) =>
        ReferenceEquals(first, second) ||
        (first is not null &&
         second is not null &&
         first.Count == second.Count &&
         first.SequenceEqual(second));

    private static EmbeddedTaskbarNativeBounds? GetCurrentWindowBounds(
        IntPtr hwnd,
        TaskbarNativeMethods.NativeRect parentRect)
    {
        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return null;
        }

        return new EmbeddedTaskbarNativeBounds(
            windowRect.Left - parentRect.Left,
            windowRect.Top - parentRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
    }

    // 已停用：歌词窗口改为收缩宽度避让系统元素，不再挤压任务栏应用图标区。
    // 方法与其恢复逻辑保留，便于将来必要时回退到挤压式让位。
    private void SqueezeTaskList(IntPtr parent, AppSettings settings)
    {
        if (_taskListHandle == IntPtr.Zero || !IsWindow(_taskListHandle))
        {
            var previousTaskListHandle = _taskListHandle;
            _taskListHandle = FindTaskList(parent);
            if (_taskListHandle != previousTaskListHandle)
            {
                _originalTaskListBounds = default;
                _lastTaskListBounds = null;
                _taskListSqueezed = false;
            }
        }

        if (_taskListHandle == IntPtr.Zero ||
            !GetWindowRect(_taskListHandle, out var taskListRect) ||
            !GetWindowRect(parent, out var parentRect))
        {
            return;
        }

        if (!_taskListSqueezed)
        {
            _originalTaskListBounds = new EmbeddedTaskbarNativeBounds(
                taskListRect.Left - parentRect.Left,
                taskListRect.Top - parentRect.Top,
                taskListRect.Right - taskListRect.Left,
                taskListRect.Bottom - taskListRect.Top);
        }

        var pixelsPerDip = GetPixelsPerDipForHandle(parent);
        var reservedWidth = (int)Math.Round(
            (AppSettings.ClampEffectiveWindowWidth(
                settings.WindowWidth,
                settings.LyricsLayoutScalePercent,
                (parentRect.Right - parentRect.Left) / pixelsPerDip) +
                Math.Max(0, settings.XOffset)) * pixelsPerDip,
            MidpointRounding.AwayFromZero);
        var targetWidth = Math.Clamp(
            _originalTaskListBounds.Width - reservedWidth,
            0,
            _originalTaskListBounds.Width);

        var targetBounds = new EmbeddedTaskbarNativeBounds(
            _originalTaskListBounds.Left,
            _originalTaskListBounds.Top,
            targetWidth,
            _originalTaskListBounds.Height);
        if (!EmbeddedTaskbarLayoutCalculator.NeedsNativeBoundsUpdate(_lastTaskListBounds, targetBounds))
        {
            _taskListSqueezed = true;
            return;
        }

        var squeezed = MoveWindow(
            _taskListHandle,
            targetBounds.Left,
            targetBounds.Top,
            targetBounds.Width,
            targetBounds.Height,
            true);
        if (squeezed)
        {
            _lastTaskListBounds = targetBounds;
            _taskListSqueezed = true;
        }
    }

    private void RestoreTaskList()
    {
        if (_taskListSqueezed && _taskListHandle != IntPtr.Zero && IsWindow(_taskListHandle))
        {
            _ = MoveWindow(
                _taskListHandle,
                _originalTaskListBounds.Left,
                _originalTaskListBounds.Top,
                _originalTaskListBounds.Width,
                _originalTaskListBounds.Height,
                true);
        }

        _taskListSqueezed = false;
        _lastTaskListBounds = null;
    }

    private void CaptureOriginalStyles(IntPtr hwnd)
    {
        _originalStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        _originalExtendedStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _hasOriginalStyles = true;
    }

    private EmbeddedTaskbarAttachResult KeepExistingAttachmentIfValid(
        Window window,
        IntPtr hwnd,
        AppSettings settings,
        DisplayMonitor? targetDisplay)
    {
        var parentIsValid = _taskbarHandle != IntPtr.Zero &&
            IsWindow(_taskbarHandle) &&
            _parentHandle != IntPtr.Zero &&
            IsWindow(_parentHandle) &&
            GetAncestor(hwnd, GetAncestorParent) == _parentHandle;
        if (!EmbeddedTaskbarEmbeddingPolicy.ShouldKeepExistingAttachment(
                _windowHandle == hwnd,
                IsSameTargetDisplay(targetDisplay),
                parentIsValid))
        {
            return EmbeddedTaskbarAttachResult.Unavailable;
        }

        return EmbeddedTaskbarEmbeddingPolicy.FromPositionResult(
            Position(hwnd, _parentHandle, _taskbarHandle, window, settings, targetDisplay));
    }

    private bool HasAttachmentForDifferentTarget(IntPtr hwnd, DisplayMonitor? targetDisplay) =>
        _windowHandle == hwnd &&
        _parentHandle != IntPtr.Zero &&
        EmbeddedTaskbarEmbeddingPolicy.RequiresWindowReplacement(_attachedDisplayTarget, targetDisplay);

    private bool IsSameTargetDisplay(DisplayMonitor? targetDisplay) =>
        _attachedDisplayTarget is { } attachedTarget && attachedTarget.Matches(targetDisplay);

    private static bool IsWindows11() =>
        Environment.OSVersion.Version.Major == 10 &&
        Environment.OSVersion.Version.Build >= 22000;

    private static ITaskbarObstructionProbe CreateObstructionProbe() =>
        IsWindows11()
            ? new Win11TaskbarObstructionProbe()
            : new Win10TaskbarObstructionProbe();

    private void ReportAttachOutcome(EmbeddedTaskbarAttachResult result, string reason)
    {
        var summary = $"{result} Reason={reason}";
        if (string.Equals(summary, _lastAttachDiagnostic, StringComparison.Ordinal))
        {
            return;
        }

        _lastAttachDiagnostic = summary;
        Log.Diagnostic("EMBED", $"AttachResult={summary}");
    }

    private static string DescribeTarget(DisplayMonitor? targetDisplay) =>
        targetDisplay?.Id ?? "primary";

    private static void ApplyChildWindowStyle(IntPtr hwnd)
    {
        var style = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(
            hwnd,
            GwlStyle,
            new IntPtr((style & ~WsPopup) | WsChild | WsVisible));

        var extendedStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(
            hwnd,
            GwlExStyle,
            new IntPtr((extendedStyle & ~WsExToolWindow) | WsExNoActivate));
    }

    private void RestoreTopLevelStyles(IntPtr hwnd)
    {
        if (_hasOriginalStyles)
        {
            _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(_originalStyle));
            _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(_originalExtendedStyle));
            return;
        }

        var style = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlStyle, new IntPtr((style & ~WsChild) | WsPopup));
        var extendedStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(extendedStyle & ~WsExNoActivate));
    }

    private static IntPtr FindTaskbar(DisplayMonitor? targetDisplay, out string diagnostic)
    {
        var primaryTaskbar = FindWindow("Shell_TrayWnd", null);
        if (targetDisplay is null)
        {
            diagnostic = $"Target=primary Primary={(primaryTaskbar != IntPtr.Zero)}";
            return primaryTaskbar;
        }

        var taskbars = new List<IntPtr>();
        if (primaryTaskbar != IntPtr.Zero)
        {
            taskbars.Add(primaryTaskbar);
        }

        var secondaryCount = 0;
        var secondaryTaskbar = IntPtr.Zero;
        while (true)
        {
            secondaryTaskbar = FindWindowEx(
                IntPtr.Zero,
                secondaryTaskbar,
                "Shell_SecondaryTrayWnd",
                null);
            if (secondaryTaskbar == IntPtr.Zero)
            {
                break;
            }

            secondaryCount++;
            taskbars.Add(secondaryTaskbar);
        }

        var match = taskbars
            .Select(handle => new { Handle = handle, Score = CalculateTaskbarScore(handle, targetDisplay.Bounds) })
            .OrderByDescending(candidate => candidate.Score.IntersectionArea)
            .ThenBy(candidate => candidate.Score.CenterDistanceSquared)
            .FirstOrDefault();
        var bounds = targetDisplay.Bounds;
        diagnostic =
            $"Target={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom} " +
            $"Primary={(primaryTaskbar != IntPtr.Zero)} Secondary={secondaryCount} " +
            $"BestIntersection={match?.Score.IntersectionArea ?? 0}";
        return match is not null && match.Score.IntersectionArea > 0
            ? match.Handle
            : IntPtr.Zero;
    }

    private static (long IntersectionArea, long CenterDistanceSquared) CalculateTaskbarScore(
        IntPtr taskbar,
        NativeRect displayBounds)
    {
        if (!GetWindowRect(taskbar, out var taskbarBounds))
        {
            return (0, long.MaxValue);
        }

        var intersectionArea = EmbeddedTaskbarLayoutCalculator.CalculateIntersectionArea(
            new NativeRect(taskbarBounds.Left, taskbarBounds.Top, taskbarBounds.Right, taskbarBounds.Bottom),
            displayBounds);
        var taskbarCenterX = ((long)taskbarBounds.Left + taskbarBounds.Right) / 2;
        var taskbarCenterY = ((long)taskbarBounds.Top + taskbarBounds.Bottom) / 2;
        var displayCenterX = ((long)displayBounds.Left + displayBounds.Right) / 2;
        var displayCenterY = ((long)displayBounds.Top + displayBounds.Bottom) / 2;
        var deltaX = taskbarCenterX - displayCenterX;
        var deltaY = taskbarCenterY - displayCenterY;
        return (intersectionArea, (deltaX * deltaX) + (deltaY * deltaY));
    }

    private static IntPtr FindTaskbarContainer(IntPtr taskbar)
    {
        var reBar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        return reBar != IntPtr.Zero ? reBar : FindWindowEx(taskbar, IntPtr.Zero, "WorkerW", null);
    }

    private static IntPtr FindTaskList(IntPtr container)
    {
        var taskList = FindWindowEx(container, IntPtr.Zero, "MSTaskSwWClass", null);
        return taskList != IntPtr.Zero
            ? taskList
            : FindWindowEx(container, IntPtr.Zero, "MSTaskListWClass", null);
    }

    private static double GetPixelsPerDipForHandle(IntPtr hwnd)
    {
        var dpi = TaskbarNativeMethods.GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(
        IntPtr parent,
        IntPtr childAfter,
        string? className,
        string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out TaskbarNativeMethods.NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("ole32.dll", SetLastError = true)]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
}

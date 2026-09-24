namespace TaskbarLyrics.App;

// 任务栏上被系统元素占用的水平区间。Left/Right 为区间左右边界，单位与
// 语义均为逻辑 DIP，相对任务栏客户区左缘（含 clamp 到 [0, TaskbarWidth]）。
internal readonly record struct TaskbarObstruction(double Left, double Right)
{
    public double Width => Right - Left;
}

// 探测系统元素所需的原生上下文。TaskbarWidth/TaskbarHeight 均为逻辑 DIP。
internal readonly record struct TaskbarObstructionProbeContext(
    IntPtr TaskbarHandle,
    IntPtr ContainerHandle,
    IntPtr LyricsWindowHandle,
    double PixelsPerDip,
    double TaskbarWidth,
    double TaskbarHeight);

// 产出任务栏上被系统元素占用的水平区间。实现必须把原生物理像素在单一
// 命名边界统一换算为逻辑 DIP；探测失败时应返回空集合，使上层安全降级为
// 「不收缩、沿用原宽度」。探测属于轮询路径，实现内部不得写诊断日志。
internal interface ITaskbarObstructionProbe
{
    IReadOnlyList<TaskbarObstruction> Probe(TaskbarObstructionProbeContext context);
}

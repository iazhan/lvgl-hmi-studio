namespace HmiEditor.App.Services;

/// <summary>
/// 预览宿主的**心跳判据**（纯函数，便于单测；视图只负责按它做动作）。
///
/// <para><b>为什么用"帧序号不动"而不是 PING/PONG</b>：隔离宿主的共享内存头里
/// 已经有 <c>seq</c>（每帧 +1）。它不动只有两种可能：进程没了，或者 tick 循环卡住
/// （脚本死循环、原生崩溃前的挂起）。两种情况对用户是同一件事——预览死了。
/// 加一套 PING/PONG 只是把同一个判据换成更长的链路。</para>
///
/// <para><b>为什么 3 秒</b>：静态页面也照样每帧 +1（宿主无条件拷帧并递增），
/// 所以"3 秒没有新帧"不会是"画面没变化"造成的误判；而一次页面切换/建页最坏
/// 也就几百毫秒，3 秒足够宽松。</para>
/// </summary>
public static class PreviewHealth
{
    /// <summary>多久没有新帧就判定为"卡死"。</summary>
    public const int StallTimeoutMs = 3000;

    /// <summary>自动重启的统计窗口：窗口内超过次数上限就不再自动重启。</summary>
    public const int RestartWindowMs = 30_000;

    /// <summary>窗口内最多自动重启几次（第 4 次直接放弃，交给人）。</summary>
    public const int MaxAutoRestarts = 3;

    /// <summary>宿主是否已经不可用：进程没了，或帧序号超过阈值没有推进。</summary>
    public static bool IsStalled(bool processAlive, long sinceFrameChangeMs, long nowMs)
        => !processAlive || nowMs - sinceFrameChangeMs > StallTimeoutMs;

    /// <summary>
    /// 还能不能再自动重启一次。<paramref name="firstCrashMs"/> 为 0 表示还没崩过
    /// （本次是第一次），窗口从这里起算。
    /// </summary>
    public static bool ShouldAutoRestart(int restartsDone, long firstCrashMs, long nowMs)
        => restartsDone < MaxAutoRestarts &&
           (firstCrashMs == 0 || nowMs - firstCrashMs <= RestartWindowMs);
}

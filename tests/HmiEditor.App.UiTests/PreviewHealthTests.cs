using HmiEditor.App.Services;
using Xunit;

namespace HmiEditor.App.UiTests;

/// <summary>
/// 心跳判据的边界：这些数字决定"预览什么时候被判死、什么时候放弃自动重启"，
/// 改阈值必须有意识地改这里的断言，而不是随手调常量。
/// </summary>
public class PreviewHealthTests
{
    [Fact]
    public void ProcessGone_IsAlwaysStalled()
        => Assert.True(PreviewHealth.IsStalled(processAlive: false, sinceFrameChangeMs: 0, nowMs: 10));

    [Fact]
    public void FrameMoving_IsNotStalled_EvenAfterLongIdle()
        => Assert.False(PreviewHealth.IsStalled(true, sinceFrameChangeMs: 9_000, nowMs: 10_000));

    [Fact]
    public void FrameSilentBeyondTimeout_IsStalled()
        => Assert.True(PreviewHealth.IsStalled(true, sinceFrameChangeMs: 0,
            nowMs: PreviewHealth.StallTimeoutMs + 1));

    [Fact]
    public void FrameSilentExactlyAtTimeout_IsStillAlive()
        => Assert.False(PreviewHealth.IsStalled(true, sinceFrameChangeMs: 0,
            nowMs: PreviewHealth.StallTimeoutMs));

    [Fact]
    public void FirstCrash_IsAlwaysAllowed()
        => Assert.True(PreviewHealth.ShouldAutoRestart(0, firstCrashMs: 0, nowMs: 1));

    [Fact]
    public void UnderTheCap_InsideTheWindow_IsAllowed()
        => Assert.True(PreviewHealth.ShouldAutoRestart(2, firstCrashMs: 1_000, nowMs: 20_000));

    [Fact]
    public void OverTheCap_IsRefused()
        => Assert.False(PreviewHealth.ShouldAutoRestart(PreviewHealth.MaxAutoRestarts,
            firstCrashMs: 1_000, nowMs: 2_000));

    [Fact]
    public void OutsideTheWindow_IsRefused()
        => Assert.False(PreviewHealth.ShouldAutoRestart(1, firstCrashMs: 1_000,
            nowMs: 1_000 + PreviewHealth.RestartWindowMs + 1));
}

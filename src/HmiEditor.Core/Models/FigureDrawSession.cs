namespace HmiEditor.Core.Models;

/// <summary>绘制会话一次输入后的结果。</summary>
public enum FigureDrawOutcome
{
    /// <summary>还需继续取点（预览照旧）。</summary>
    Pending,

    /// <summary>取点已够，可以落控件。</summary>
    Commit,

    /// <summary>放弃：不留半成品，也不落控件。</summary>
    Cancelled,
}

/// <summary>
/// "选中工具 → 待绘制 → 首点 → 预览 → 提交/取消"的状态机。
///
/// <para>纯逻辑：只吃模型坐标点、只吐 <see cref="FigureDrawOutcome"/>，画布那边负责把
/// 结果落到控件上。这样"点几下成形""右键/ESC 结束""不足点数放弃"这些规则能被真跑，
/// 不必开窗口点鼠标。</para>
///
/// <para>三类提交时机由 <see cref="FigurePointMode"/> 决定：
/// 两点/角度/三点取满即提交；N 点式要显式 <see cref="Finish"/>（右键 / ESC）。</para>
/// </summary>
public sealed class FigureDrawSession
{
    private readonly List<PointData> _points = new();

    public FigureDrawSession(CanvasTool tool)
    {
        Tool = tool;
        Mode = tool.Mode;
    }

    public CanvasTool Tool { get; }

    public FigurePointMode Mode { get; }

    public IReadOnlyList<PointData> Points => _points;

    public bool HasPoints => _points.Count > 0;

    /// <summary>会话已结束（提交或放弃）—— 结束后再输入一律不再累积。</summary>
    public bool IsFinished { get; private set; }

    /// <summary>再取一个点。</summary>
    public FigureDrawOutcome Click(PointData modelPoint)
    {
        if (IsFinished) return FigureDrawOutcome.Cancelled;
        if (Mode == FigurePointMode.Box) return FigureDrawOutcome.Cancelled;
        if (_points.Count >= FigureGeometry.MaxPoints) return FigureDrawOutcome.Pending;

        _points.Add(modelPoint);

        // 三点式：第 3 点就是终点，取满即提交
        if (Mode == FigurePointMode.ThreePointCorner && _points.Count >= 3)
            return Complete();

        // 两点/角度：第二点成形
        if (Mode is FigurePointMode.TwoPoint or FigurePointMode.Angle && _points.Count >= 2)
            return Complete();

        return FigureDrawOutcome.Pending;
    }

    /// <summary>右键 / ESC：N 点式在此结束；点数不足则放弃（不留退化形状）。</summary>
    public FigureDrawOutcome Finish()
    {
        if (IsFinished) return FigureDrawOutcome.Cancelled;
        // 拖框工具根本不走点集 —— 就算有人误建了会话，也绝不能在"结束"时落一个 0 点控件
        if (Mode == FigurePointMode.Box) return Cancel();
        if (_points.Count < FigureGeometry.MinPoints(Mode))
        {
            IsFinished = true;
            _points.Clear();
            return FigureDrawOutcome.Cancelled;
        }
        return Complete();
    }

    /// <summary>取消（切工具 / 切页面 / 离开画布）：**永不**提交。</summary>
    public FigureDrawOutcome Cancel()
    {
        IsFinished = true;
        _points.Clear();
        return FigureDrawOutcome.Cancelled;
    }

    private FigureDrawOutcome Complete()
    {
        IsFinished = true;
        return FigureDrawOutcome.Commit;
    }
}

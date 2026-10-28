using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HmiEditor.App.Controls;

/// <summary>
/// 选中装饰层 —— 顶层 Canvas，渲染选中框 + 8 点缩放手柄。
/// 只负责绘制，交互由 DesignCanvas 处理。
/// </summary>
public class SelectionAdorner : Control
{
    public static readonly StyledProperty<Rect> SelectionBoundsProperty =
        AvaloniaProperty.Register<SelectionAdorner, Rect>(nameof(SelectionBounds));

    public static readonly StyledProperty<bool> IsSelectionVisibleProperty =
        AvaloniaProperty.Register<SelectionAdorner, bool>(nameof(IsSelectionVisible), false);

    public static readonly StyledProperty<double> HandleSizeProperty =
        AvaloniaProperty.Register<SelectionAdorner, double>(nameof(HandleSize), 8.0);

    public static readonly StyledProperty<bool> ShowMultiSelectionProperty =
        AvaloniaProperty.Register<SelectionAdorner, bool>(nameof(ShowMultiSelection), false);

    /// <summary>
    /// 画不画 8 个缩放手柄。按点图元（线/折线/弧/管道）关掉：它们的形状由顶点决定，
    /// 框只是包围盒 —— 手柄既没用，还会与端点/顶点手柄**叠在同一个像素上**，
    /// 想拖端点结果拖到了缩放。关掉后只剩一圈虚线框表示"选中了"。
    /// </summary>
    public static readonly StyledProperty<bool> ShowHandlesProperty =
        AvaloniaProperty.Register<SelectionAdorner, bool>(nameof(ShowHandles), true);

    public bool ShowHandles
    {
        get => GetValue(ShowHandlesProperty);
        set => SetValue(ShowHandlesProperty, value);
    }

    /// <summary>选中框的旋转角（度，顺时针为正）。控件设了旋转时框跟着转，否则框和位图对不上。</summary>
    public static readonly StyledProperty<double> SelectionRotationProperty =
        AvaloniaProperty.Register<SelectionAdorner, double>(nameof(SelectionRotation));

    public double SelectionRotation
    {
        get => GetValue(SelectionRotationProperty);
        set => SetValue(SelectionRotationProperty, value);
    }

    public Rect SelectionBounds
    {
        get => GetValue(SelectionBoundsProperty);
        set => SetValue(SelectionBoundsProperty, value);
    }

    public bool IsSelectionVisible
    {
        get => GetValue(IsSelectionVisibleProperty);
        set => SetValue(IsSelectionVisibleProperty, value);
    }

    public double HandleSize
    {
        get => GetValue(HandleSizeProperty);
        set => SetValue(HandleSizeProperty, value);
    }

    public bool ShowMultiSelection
    {
        get => GetValue(ShowMultiSelectionProperty);
        set => SetValue(ShowMultiSelectionProperty, value);
    }

    public static readonly StyledProperty<IReadOnlyList<AlignmentGuide>> AlignmentGuidesProperty =
        AvaloniaProperty.Register<SelectionAdorner, IReadOnlyList<AlignmentGuide>>(
            nameof(AlignmentGuides), Array.Empty<AlignmentGuide>());

    public IReadOnlyList<AlignmentGuide> AlignmentGuides
    {
        get => GetValue(AlignmentGuidesProperty);
        set => SetValue(AlignmentGuidesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!IsSelectionVisible) return;

        // 选择框虚线（控件带旋转时，框绕自身中心一起转）
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 1)
        {
            DashStyle = DashStyle.Dash
        };

        var angle = SelectionRotation % 360;
        if (angle == 0)
        {
            DrawFrameAndHandles(context, pen);
        }
        else
        {
            // 参考线是视口坐标，不能跟着转 —— 所以只在框与手柄这一小段里压入变换
            using (context.PushTransform(RotationAround(SelectionBounds.Center, angle)))
                DrawFrameAndHandles(context, pen);
        }

        // 绘制对齐参考线（虚线红色）
        var guidePen = new Pen(new SolidColorBrush(Color.FromRgb(255, 80, 80)), 0.8)
        {
            DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0)
        };

        var guides = AlignmentGuides;
        if (guides != null && guides.Count > 0)
        {
            foreach (var guide in guides)
            {
                if (guide.IsHorizontal)
                {
                    context.DrawLine(guidePen,
                        new Point(guide.Start, guide.Position),
                        new Point(guide.End, guide.Position));
                }
                else
                {
                    context.DrawLine(guidePen,
                        new Point(guide.Position, guide.Start),
                        new Point(guide.Position, guide.End));
                }
            }
        }
    }

    /// <summary>画虚线框 + 8 个缩放手柄（调用方可能已经压入了旋转变换）。</summary>
    private void DrawFrameAndHandles(DrawingContext context, Pen pen)
    {
        context.DrawRectangle(null, pen, SelectionBounds);

        // 多选 / 按点图元 → 不绘制手柄
        if (ShowMultiSelection || !ShowHandles) return;

        var handleBrush = Brushes.White;
        var handlePen = new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 1);
        var hs = HandleSize;

        foreach (var pt in GetHandlePositions())
        {
            var rect = new Rect(pt.X - hs / 2, pt.Y - hs / 2, hs, hs);
            context.DrawRectangle(handleBrush, handlePen, rect);
        }
    }

    /// <summary>绕 <paramref name="center"/> 旋转 <paramref name="degrees"/> 度的变换矩阵。</summary>
    private static Matrix RotationAround(Point center, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        return Matrix.CreateTranslation(-center.X, -center.Y)
               * Matrix.CreateRotation(rad)
               * Matrix.CreateTranslation(center.X, center.Y);
    }

    /// <summary>
    /// 返回 8 个手柄的中心位置。
    /// </summary>
    public Point[] GetHandlePositions()
    {
        var b = SelectionBounds;
        return new[]
        {
            b.TopLeft,                                      // 0: 左上
            new Point(b.Center.X, b.Top),                   // 1: 上中
            b.TopRight,                                     // 2: 右上
            new Point(b.Right, b.Center.Y),                 // 3: 右中
            b.BottomRight,                                  // 4: 右下
            new Point(b.Center.X, b.Bottom),                // 5: 下中
            b.BottomLeft,                                   // 6: 左下
            new Point(b.Left, b.Center.Y),                  // 7: 左中
        };
    }

    /// <summary>
    /// 返回 4 个角手柄的位置索引（0, 2, 4, 6）。
    /// </summary>
    public int[] GetCornerHandleIndices() => new[] { 0, 2, 4, 6 };

    /// <summary>
    /// 返回 4 个边中点手柄的位置索引（1, 3, 5, 7）。
    /// </summary>
    public int[] GetEdgeHandleIndices() => new[] { 1, 3, 5, 7 };
}

/// <summary>对齐参考线数据（视口坐标）。</summary>
public readonly record struct AlignmentGuide(double Position, bool IsHorizontal, double Start, double End);

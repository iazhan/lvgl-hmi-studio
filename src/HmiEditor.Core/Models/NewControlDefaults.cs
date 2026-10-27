using System.Collections.Generic;
using System.Linq;
using HmiEditor.Core.Services;

namespace HmiEditor.Core.Models;

/// <summary>
/// 新建控件「不动任何参数」时的那套值 —— **画布与判据共用的单一来源**。
///
/// <para><b>为什么放在 Core 而不是画布里</b>：这些值都会显式写进导出产物
/// （<c>ChengeWidgetMapper</c> 无条件写颜色/字号/量程），也就是说"拖出来什么样"等于
/// "板子上什么样"。放在 <c>DesignCanvas.BuildControl</c> 里只有起 UI 才验得到，导出
/// 侧一旦漂移没人看得见；搬到 Core 之后 <c>NewControlDefaultsExportTests</c> 能逐类型
/// 钉住"新建 → 产物"的每一个数字。</para>
///
/// <para><b>与契约 <c>spec/widget-schema.json</c> 的关系</b>：这里给的是**模板初值**，
/// 契约的 <c>default</c> 描述的是**键缺失**的语义（手写工程 / 其它工具的角度）。
/// 两者可以不同，但每个不同都要说得出理由，并且必须满足两条：
/// ① 值一定写进产物（面板显示 = 板端生效）；② 面板上能看见这一行。
/// 逐条对照与取舍见 <c>docs/chenge-rt/50</c> §7。</para>
/// </summary>
public static class NewControlDefaults
{
    /// <summary>
    /// 新建时带标题文字的类型。开关不在此列：它的 <c>Text</c> 会导出成
    /// <c>text_on</c>，运行时把非空的 <c>text_on</c> 画在**控件框右边**（
    /// <c>widget_action.c</c> 的 build_switch），新建就带一个"开关1"贴在旁边纯属噪声。
    /// 折线/直线/圆弧没有文字层，同样不给。
    /// </summary>
    public static bool HasCaption(HmiControlType type) => type is not (
        HmiControlType.Switch or HmiControlType.Line or HmiControlType.Polyline or HmiControlType.Arc);

    /// <summary>
    /// 把模板初值写进 <paramref name="control"/>（不改 Name/Text 这类本地化文案）。
    /// <paramref name="points"/> 是绘图工具落下的顶点（折线/直线用）。
    /// </summary>
    public static void Apply(HmiControl control, IReadOnlyList<PointData>? points = null)
    {
        // 底色：没有底色的控件在 800×480 深色屏上等于看不见（Spinner/Label/CheckBox 例外）
        control.BgColor = ControlDefaults.NewControlBgColor(control.Type);

        // 圆角/边框宽度 = 运行时"吃控件主题"的那套外观。导出器把这两个值原样写进产物，
        // 所以面板上默认 0 会把按钮/输入框/表格的圆角与 1px 边框一起抹掉。
        // 数值是对每类控件"不给键 vs 给定值"逐像素比对反推的（不是抄 LVGL 常量）。
        var (radius, border) = AppearanceDefaults.For(control.Type, control.Width, control.Height);
        control.Radius = radius;
        control.BorderWidth = border;

        switch (control.Type)
        {
            case HmiControlType.Rectangle:
            case HmiControlType.Ellipse:
                if (control.BorderWidth <= 0) control.BorderWidth = 1;
                if (string.IsNullOrEmpty(control.BorderColor)) control.BorderColor = "#64748b";
                break;

            case HmiControlType.Polyline:
                if (control.BorderWidth <= 0) control.BorderWidth = 2;
                control.Points = points?.ToList() ?? new List<PointData>
                {
                    new(10, 10),
                    new(control.Width - 10, control.Height - 10),
                };
                break;

            case HmiControlType.Line:
                if (control.BorderWidth <= 0) control.BorderWidth = 2;
                // 点击式绘制：保存两端点局部坐标；拖拽/工具箱路径保持 null（渲染回退对角线）
                if (points is { Count: >= 2 }) control.Points = points.Take(2).ToList();
                break;

            case HmiControlType.Arc:
                if (control.BorderWidth <= 0) control.BorderWidth = 10;
                if (control.MinValue == 0 && control.MaxValue == 100)
                {
                    // 角度范围 0..360°，指示弧默认 270°（3/4 圆）
                    control.MaxValue = 360;
                    control.Value = 270;
                }
                if (string.IsNullOrEmpty(control.IndicatorColor)) control.IndicatorColor = "#38bdf8";
                break;

            case HmiControlType.TextArea:
                // 量程 0..100 是滑块/仪表的模板值，套到输入框上会变成"输不进 500"的隐形上限；
                // 契约给的输入框语义是"基本不限"（缺上限时不做上限校验），这里按 0..1000000 写。
                control.MinValue = 0;
                control.MaxValue = 1000000;
                // 契约 input.max_len 缺省 12（键盘上限 24）；0 在模型里表示"不限"，不写它会
                // 导成 64 —— 面板显示 0、板上却按 24 截断。
                if (control.MaxLength <= 0) control.MaxLength = 12;
                break;

            case HmiControlType.RealtimeCurve:
                // 实时曲线的窗口按契约缺省 60s（1 分钟）：30 分钟 × 1s 采样 = 1800 个点，
                // 运行时只留 300 点（widget_curve.c），X 轴也是按"点数 × 采样"标的 ——
                // 面板写 30 分钟、板上只画得出 5 分钟。历史曲线的 30 分钟是另一回事
                // （窗口走 range.last_min，点数由存盘数据决定），不动。
                control.SpanMin = 1;
                break;

            case HmiControlType.Value:
                // 读数是"值显示区"，契约给的是 20（label 是 16）；用模型的 14 会让板上的
                // 读数与面板那行"字号 14"一致、却与契约缺省差一档。
                if (control.FontSize == HmiControl.DefaultFontSize) control.FontSize = 20;
                break;
        }
    }
}

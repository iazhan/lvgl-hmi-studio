using System;

namespace HmiEditor.Core.Models;

/// <summary>
/// 新建控件时「圆角 / 边框宽度」的**出厂值**。
///
/// <para><b>为什么需要</b>：导出器把面板上的值原样写进产物（见
/// <see cref="AppearanceSupport.HasBoxBorder"/> 的说明），而运行时此前是"没给键就用
/// 控件自带外观" —— 按钮 13、输入框 4+1px、表格 4+1px、开关/滑块是圆头。如果新建控件
/// 的模型默认是 0，面板显示 0、导出写 0，按钮和输入框当场变直角/无边框。</para>
///
/// <para><b>数值怎么来的</b>：对每类控件渲染两次 —— 一次不给这个键、一次给某个值，
/// 逐像素比对到完全一致，那个值就是"出厂外观"（不是抄 LVGL 主题常量：主题还乘了
/// dpi 缩放，抄常量会差几个像素）。量测脚本与结果在
/// <c>chenge-runtime/tools/appearance_probe.py</c> 的用法说明里。</para>
/// </summary>
public static class AppearanceDefaults
{
    /// <summary>该类型的出厂圆角与边框宽度；<paramref name="w"/>/<paramref name="h"/> 用于圆头类控件。</summary>
    public static (int Radius, int BorderWidth) For(HmiControlType type, double w, double h) => type switch
    {
        // 主题按 dpi 缩放后的 13（不是裸常量 8）
        HmiControlType.Button => (13, 0),
        // 轨道/开关的"圆头" = 短边的一半；写成数值才能表达"面板说多少就是多少"
        HmiControlType.Slider or HmiControlType.Bar or HmiControlType.Switch
            => (RoundCap(w, h), 0),
        // 方框类：自带 1px 边框（输入框/下拉框/表格/消息条），丢了就没有"这是个框"的暗示
        HmiControlType.TextArea => (4, 1),
        HmiControlType.Dropdown => (10, 1),
        HmiControlType.Roller => (10, 0),
        HmiControlType.Table => (4, 1),
        HmiControlType.Message => (6, 1),
        HmiControlType.HistoryTable or HmiControlType.RecipeTable
            or HmiControlType.HistoryCurve or HmiControlType.RealtimeCurve
            or HmiControlType.XyCurve => (0, 1),
        // 复选框的圆角落在方框（INDICATOR）上，主题给 4
        HmiControlType.CheckBox => (4, 0),
        _ => (0, 0),
    };

    /// <summary>短边的一半（LVGL 的"圆头"语义）；<c>Math.Round</c> 会把它算成 0 的极端情形也不会。</summary>
    private static int RoundCap(double w, double h)
    {
        var half = Math.Min(w, h) / 2.0;
        return half <= 0 ? 0 : (int)Math.Round(half);
    }
}

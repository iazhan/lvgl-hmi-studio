namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板的**曲线设置**（history_curve / realtime_curve 共用）。
///
/// <para>抽成独立分部文件有两个原因：一是 <c>PropertyPanelView.Parameters.cs</c> 有 500 行上限
/// （<c>SingleFileSizeGuardTests</c>），二是曲线这一批与其它控件的「功能参数」不是一类东西 ——
/// 它整块显隐、整块回填，放在一起反而更难读。六个颜色行不在此列（见外观样式组）。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>曲线设置那一整块 Expander 的显隐（连带清掉本块所有行的可见性）。</summary>
    private void ShowCurveRows(bool visible)
    {
        // 六个颜色行（轴线/底色/外框/网格/刻度/标签）不在本块里：它们是外观，
        // 显隐由 ShowAppearanceRows 的 isCurve 判据负责。
        SetParamRow(LblCurveShowGrid, CurveGridCheck, visible);
        SetParamRow(LblCurveLegend, CurveLegendCheck, visible);
        SetParamRow(LblCurveShowAxis, CurveShowAxisCheck, visible);
        SetParamRow(LblCurveShowLabels, CurveShowLabelsCheck, visible);
        // 曲线刻度（props.show_ticks）：契约缺省 true，而这行以前对曲线不显示，
        // 于是导出恒为 false —— 板上永远没有刻度线，面板上也找不到开关
        SetParamRow(LblShowTicks, ShowTicksCheck, visible);
        // 曲线方向：运行时只接受 lr（rl 直接拒绝整页），面板不给入口，避免"改了不生效"
        SetParamRow(LblCurveDirection, CurveDirectionCombo, false);
        SetParamRow(LblCurveXFormat, CurveXFormatCombo, visible);
        SetParamRow(LblCurveXDiv, CurveXDivBox, visible);
        SetParamRow(LblCurveXMinor, CurveXMinorBox, visible);
        SetParamRow(LblCurveXLabelStep, CurveXLabelStepBox, visible);
        SetParamRow(LblCurveYDiv, CurveYDivBox, visible);
        SetParamRow(LblCurveYMinor, CurveYMinorBox, visible);
        SetParamRow(LblCurveYDecimals, CurveYDecimalsBox, visible);
        SetParamRow(LblCurveYDigits, CurveYDigitsBox, visible);
        SetParamRow(LblCurveY2Min, CurveY2MinBox, visible);
        SetParamRow(LblCurveY2Max, CurveY2MaxBox, visible);
        SetParamRow(LblCurveY2Decimals, CurveY2DecimalsBox, visible);
        SetParamRow(LblCurveY2Digits, CurveY2DigitsBox, visible);
        SetParamRow(LblCurveLastMin, CurveLastMinBox, visible);
        CurveParamExpander.IsVisible = visible;
    }

    /// <summary>把曲线字段回填到面板。</summary>
    private void FillCurveFields(HmiControl control)
    {
        CurveAxisColorBox.Text = control.CurveAxisColor ?? "";
        CurveBgColorBox.Text = control.CurveBgColor ?? "";
        CurveBorderColorBox.Text = control.CurveBorderColor ?? "";
        CurveGridColorBox.Text = control.CurveGridColor ?? "";
        CurveTickColorBox.Text = control.CurveTickColor ?? "";
        CurveLabelColorBox.Text = control.CurveLabelColor ?? "";
        CurveGridCheck.IsChecked = control.CurveGrid;
        CurveLegendCheck.IsChecked = control.CurveLegend;
        CurveShowAxisCheck.IsChecked = control.CurveShowAxis;
        CurveShowLabelsCheck.IsChecked = control.CurveShowLabels;
        SelectComboByTag(CurveDirectionCombo, control.CurveDirection, 0);
        SelectComboByTag(CurveXFormatCombo, control.CurveXFormat, 0);
        CurveXDivBox.Value = control.CurveXDiv;
        CurveXMinorBox.Value = control.CurveXMinor;
        CurveXLabelStepBox.Value = control.CurveXLabelStep;
        CurveYDivBox.Value = control.CurveYDiv;
        CurveYMinorBox.Value = control.CurveYMinor;
        CurveYDecimalsBox.Value = control.CurveYDecimals;
        CurveYDigitsBox.Value = control.CurveYDigits;
        CurveY2MinBox.Value = control.CurveY2Min;
        CurveY2MaxBox.Value = control.CurveY2Max;
        CurveY2DecimalsBox.Value = control.CurveY2Decimals;
        CurveY2DigitsBox.Value = control.CurveY2Digits;
        CurveLastMinBox.Value = control.CurveRangeLastMin;
    }
}

namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板里**数据类控件**的行：图表 / XY 曲线 / 历史表 / 配方表 / ASCII 显示。
/// 共同点是"没有数据源就没有内容"——它们的键大半指向变量、数据组或列定义。
///
/// <para>抽出来的原因同曲线、报警：<c>PropertyPanelView.Parameters.cs</c> 有 500 行上限
/// （<c>SingleFileSizeGuardTests</c>），而这几类控件都是整块显隐、整块回填。</para>
/// </summary>
public partial class PropertyPanelView
{
    private void ShowChartRows(bool visible)
    {
        SetParamRow(LblChartKind, ChartKindCombo, visible);
        SetParamRow(LblChartSeries, ChartSeriesBox, visible);
        SetParamRow(LblChartLegend, ChartLegendCheck, visible);
        SetParamRow(LblChartValue, ChartValueCheck, visible);
    }

    private void ShowXyRows(bool visible)
    {
        SetParamRow(LblXyTagX, XyTagXBox, visible);
        SetParamRow(LblXyTagY, XyTagYBox, visible);
        SetParamRow(LblXyDot, XyDotCheck, visible);
        SetParamRow(LblXyLine, XyLineCheck, visible);
    }

    private void ShowHistTableRows(bool visible)
    {
        SetParamRow(LblHistColumns, HistColumnsBox, visible);
        SetParamRow(LblHistGroup, HistGroupBox, visible);
        SetParamRow(LblHistRowHeight, HistRowHeightBox, visible);
        SetParamRow(LblHistScrollbar, HistScrollbarCheck, visible);
    }

    private void ShowRecipeRows(bool visible)
    {
        SetParamRow(LblRecipeColumns, RecipeColumnsBox, visible);
        SetParamRow(LblRecipeGroup, RecipeGroupBox, visible);
        SetParamRow(LblRecipeUnit, RecipeUnitCheck, visible);
    }

    private void ShowAsciiRows(bool visible)
    {
        SetParamRow(LblAsciiEncoding, AsciiEncodingCombo, visible);
        SetParamRow(LblAsciiMaxLen, AsciiMaxLenBox, visible);
    }

    /// <summary>数据类控件的字段回填（各类共用一个方法；写回时才按可见性分别判）。</summary>
    private void FillDataViewFields(HmiControl control)
    {
        SelectComboByTag(ChartKindCombo, control.ChartKind, 0);
        ChartSeriesBox.Text = control.ChartSeries ?? "";
        ChartLegendCheck.IsChecked = control.ChartShowLegend;
        ChartValueCheck.IsChecked = control.ChartShowValue;

        XyTagXBox.Text = control.XyTagX ?? "";
        XyTagYBox.Text = control.XyTagY ?? "";
        XyDotCheck.IsChecked = control.XyDot;
        XyLineCheck.IsChecked = control.XyLine;

        HistColumnsBox.Text = control.HistTableColumns ?? "";
        HistGroupBox.Text = control.HistTableGroup ?? "";
        HistRowHeightBox.Value = control.HistTableRowHeight;
        HistScrollbarCheck.IsChecked = control.HistTableScrollbar;

        RecipeColumnsBox.Text = control.RecipeColumns ?? "";
        RecipeGroupBox.Text = control.RecipeGroup ?? "";
        RecipeUnitCheck.IsChecked = control.RecipeShowUnit;

        AsciiMaxLenBox.Value = control.AsciiMaxLen;
        SelectComboByTag(AsciiEncodingCombo, control.AsciiEncoding, 0);
    }
}

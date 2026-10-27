namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 数据类控件（图表 / XY 曲线 / 历史表 / 配方表 / ASCII 显示）的写回。
/// 抽出来的原因：<c>Changes.cs</c> 有 500 行上限。
/// </summary>
public partial class PropertyPanelView
{
    private void WriteDataViewFields()
    {
        if (ChartKindCombo.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ChartKind), GetComboTag(ChartKindCombo), v => _currentControl.ChartKind = v);
        if (ChartSeriesBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ChartSeries), string.IsNullOrWhiteSpace(ChartSeriesBox.Text) ? null : ChartSeriesBox.Text.Trim(), v => _currentControl.ChartSeries = v);
        if (ChartLegendCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ChartShowLegend), ChartLegendCheck.IsChecked == true, v => _currentControl.ChartShowLegend = v);
        if (ChartValueCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.ChartShowValue), ChartValueCheck.IsChecked == true, v => _currentControl.ChartShowValue = v);
        if (XyTagXBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.XyTagX), string.IsNullOrWhiteSpace(XyTagXBox.Text) ? null : XyTagXBox.Text.Trim(), v => _currentControl.XyTagX = v);
        if (XyTagYBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.XyTagY), string.IsNullOrWhiteSpace(XyTagYBox.Text) ? null : XyTagYBox.Text.Trim(), v => _currentControl.XyTagY = v);
        if (XyDotCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.XyDot), XyDotCheck.IsChecked == true, v => _currentControl.XyDot = v);
        if (XyLineCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.XyLine), XyLineCheck.IsChecked == true, v => _currentControl.XyLine = v);
        if (HistColumnsBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.HistTableColumns), string.IsNullOrWhiteSpace(HistColumnsBox.Text) ? null : HistColumnsBox.Text.Trim(), v => _currentControl.HistTableColumns = v);
        if (HistGroupBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.HistTableGroup), string.IsNullOrWhiteSpace(HistGroupBox.Text) ? null : HistGroupBox.Text.Trim(), v => _currentControl.HistTableGroup = v);
        if (HistRowHeightBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.HistTableRowHeight), (int)HistRowHeightBox.Value, v => _currentControl.HistTableRowHeight = v);
        if (HistScrollbarCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.HistTableScrollbar), HistScrollbarCheck.IsChecked == true, v => _currentControl.HistTableScrollbar = v);
        if (RecipeColumnsBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RecipeColumns), string.IsNullOrWhiteSpace(RecipeColumnsBox.Text) ? null : RecipeColumnsBox.Text.Trim(), v => _currentControl.RecipeColumns = v);
        if (RecipeGroupBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RecipeGroup), string.IsNullOrWhiteSpace(RecipeGroupBox.Text) ? null : RecipeGroupBox.Text.Trim(), v => _currentControl.RecipeGroup = v);
        if (RecipeUnitCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.RecipeShowUnit), RecipeUnitCheck.IsChecked == true, v => _currentControl.RecipeShowUnit = v);
        if (AsciiEncodingCombo.IsVisible)
            ValidateAndWrite(nameof(HmiControl.AsciiEncoding), GetComboTag(AsciiEncodingCombo), v => _currentControl.AsciiEncoding = v);
        if (AsciiMaxLenBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.AsciiMaxLen), (int)AsciiMaxLenBox.Value, v => _currentControl.AsciiMaxLen = v);
    }
}

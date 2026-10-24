# ---------- 1) 登记中英同值 ----------
p = 'tests/HmiEditor.Core.Tests/EnUsMechanicalAuditTests.cs'
s = open(p, encoding='utf-8').read()
a = '    private static readonly Dictionary<string, string> IdenticalAllowed = new(StringComparer.Ordinal)\n    {'
b = a + '''
        ["prop.detail.ascii.encoding.gbk"] = "GBK 是编码名，不是词，两语都写 GBK",
        ["prop.detail.ascii.encoding.utf8"] = "UTF-8 是编码名，不是词，两语都写 UTF-8",'''
if 'prop.detail.ascii.encoding.gbk' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8').write(s)

# ---------- 2) 批次 5 的面板行抽到独立分部文件 ----------
new_file = '''namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板里**批次 5**那几类控件的行（图表 / XY 曲线 / 历史表 / 配方表 / ASCII 显示）。
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

    /// <summary>批次 5 的字段回填（各类控件共用一个方法，按可见性分别写）。</summary>
    private void FillBatch5Fields(HmiControl control)
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
'''
open('src/HmiEditor.App/Views/PropertyPanelView.Parameters.Batch5.cs', 'w', encoding='utf-8', newline='').write(new_file)

# ---------- 3) Parameters.cs：三处换成调用 ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Parameters.cs'
s = open(p, encoding='utf-8', newline='').read()

# 隐藏区
start = s.index('        // 批次 5\n        SetParamRow(LblChartKind, ChartKindCombo, false);')
end = s.index('        SetParamRow(LblAsciiMaxLen, AsciiMaxLenBox, false);\n', start) + len('        SetParamRow(LblAsciiMaxLen, AsciiMaxLenBox, false);\n')
s = s[:start] + '''        // 批次 5（行太多，挪到 PropertyPanelView.Parameters.Batch5.cs）
        ShowChartRows(false);
        ShowXyRows(false);
        ShowHistTableRows(false);
        ShowRecipeRows(false);
        ShowAsciiRows(false);
''' + s[end:]

# 显示区
start = s.index('            case HmiControlType.Chart:')
end = s.index('            case HmiControlType.Shape:', start)
s = s[:start] + '''            case HmiControlType.Chart:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                ShowChartRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.XyCurve:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                ShowXyRows(true);
                ShowCurveRows(true);
                break;
            case HmiControlType.HistoryTable:
                ShowHistTableRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.RecipeTable:
                ShowRecipeRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.AsciiDisplay:
                ShowAsciiRows(true);
                DetailParamExpander.IsVisible = true;
                break;
''' + s[end:]

# 回填区
start = s.index('        ChartSeriesBox.Text = control.ChartSeries ?? "";')
end = s.index('        SelectComboByTag(AsciiEncodingCombo, control.AsciiEncoding, 0);\n', start) + len('        SelectComboByTag(AsciiEncodingCombo, control.AsciiEncoding, 0);\n')
s = s[:start] + '        FillBatch5Fields(control);\n' + s[end:]

open(p, 'w', encoding='utf-8', newline='').write(s)
print('lines now:', s.count('\n') + 1)

p = 'src/HmiEditor.App/Views/PropertyPanelView.Parameters.cs'
s = open(p, encoding='utf-8', newline='').read()

# ---- 1) 隐藏区：24 行 SetParamRow + CurveParamExpander.IsVisible = false ----
hide_start = s.index('        // 批次 2：曲线外观与刻度\n        SetParamRow(LblCurveAxis')
hide_end = s.index('        CurveParamExpander.IsVisible = false;\n', hide_start) + len('        CurveParamExpander.IsVisible = false;\n')
hide_block = s[hide_start:hide_end]

# ---- 2) 显示区：HistoryCurve 分支里的 24 行 ----
show_start = s.index('                // 批次 2：曲线外观与刻度\n                SetParamRow(LblCurveAxis')
show_end = s.index('                CurveParamExpander.IsVisible = true;\n', show_start) + len('                CurveParamExpander.IsVisible = true;\n')
show_block = s[show_start:show_end]

# ---- 3) 回填区 ----
fill_start = s.index('        CurveAxisColorBox.Text = control.CurveAxisColor ?? "";')
fill_end = s.index('        CurveLastMinBox.Value = control.CurveRangeLastMin;\n', fill_start) + len('        CurveLastMinBox.Value = control.CurveRangeLastMin;\n')
fill_block = s[fill_start:fill_end]

# 新文件：把三块改成方法体（缩进统一 8 空格）
def reindent(block, prefix_from, prefix_to):
    out = []
    for line in block.rstrip('\n').split('\n'):
        if line.startswith(prefix_from):
            out.append(prefix_to + line[len(prefix_from):])
        else:
            out.append(line)
    return '\n'.join(out)

hide_body = reindent(hide_block.replace('CurveParamExpander.IsVisible = false;', 'CurveParamExpander.IsVisible = false;'),
                     '        ', '        ')
show_body = reindent(show_block.replace('CurveParamExpander.IsVisible = true;', 'CurveParamExpander.IsVisible = true;'),
                     '                ', '        ')
fill_body = reindent(fill_block, '        ', '        ')

new_file = '''namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板的**曲线设置**（history_curve / realtime_curve 共用）。
///
/// <para>抽成独立分部文件有两个原因：一是 <c>PropertyPanelView.Parameters.cs</c> 有 500 行上限
/// （<c>SingleFileSizeGuardTests</c>），二是曲线那 24 项与其它控件的「功能参数」不是一类东西 ——
/// 它整块显隐、整块回填，放在一起反而更难读。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>曲线设置那一整块 Expander 的显隐（连带清掉所有行的可见性）。</summary>
    private void ShowCurveRows(bool visible)
    {
%s
    }

    /// <summary>把曲线字段回填到面板。</summary>
    private void FillCurveFields(HmiControl control)
    {
%s
    }
}
''' % (hide_body.replace('CurveParamExpander.IsVisible = false;', 'CurveParamExpander.IsVisible = visible;')
            .replace('        SetParamRow', '        SetParamRow'),
       fill_body)

open('src/HmiEditor.App/Views/PropertyPanelView.Parameters.Curve.cs', 'w', encoding='utf-8', newline='').write(new_file)

# ---- 原文件：三处替换成方法调用 ----
s = s[:hide_start] + '        ShowCurveRows(false);\n' + s[hide_end:]
show_start = s.index('                // 批次 2：曲线外观与刻度\n                SetParamRow(LblCurveAxis')
show_end = s.index('                CurveParamExpander.IsVisible = true;\n', show_start) + len('                CurveParamExpander.IsVisible = true;\n')
s = s[:show_start] + '                ShowCurveRows(true);\n' + s[show_end:]
fill_start = s.index('        CurveAxisColorBox.Text = control.CurveAxisColor ?? "";')
fill_end = s.index('        CurveLastMinBox.Value = control.CurveRangeLastMin;\n', fill_start) + len('        CurveLastMinBox.Value = control.CurveRangeLastMin;\n')
s = s[:fill_start] + '        FillCurveFields(control);\n' + s[fill_end:]

open(p, 'w', encoding='utf-8', newline='').write(s)
print('lines now:', s.count('\n') + 1)

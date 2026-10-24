p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()

start = s.index('            // 批次 5\n            if (ChartKindCombo.IsVisible)')
end = s.index('\n', s.index('v => _currentControl.AsciiMaxLen = v);', start)) + 1
block = s[start:end]

# 抠出来做成方法
body = block.replace('            // 批次 5\n', '')
body = '\n'.join(line[4:] if line.startswith('    ') else line for line in body.split('\n'))

new_file = '''namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 批次 5（图表 / XY 曲线 / 历史表 / 配方表 / ASCII 显示）的写回。
/// 抽出来的原因：<c>Changes.cs</c> 有 500 行上限。
/// </summary>
public partial class PropertyPanelView
{
    private void WriteBatch5Fields()
    {
%s
    }
}
''' % body.rstrip('\n')

open('src/HmiEditor.App/Views/PropertyPanelView.Changes.Batch5.cs', 'w', encoding='utf-8', newline='').write(new_file)

s = s[:start] + '            WriteBatch5Fields();\n' + s[end:]
open(p, 'w', encoding='utf-8', newline='').write(s)
print('changes lines now:', s.count('\n') + 1)

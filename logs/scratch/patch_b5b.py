rows = 69
Z = 'Grid.Row="%d" Grid.Column="0"'
def lbl(name, key, row):
    return ('''                        <TextBlock x:Name="Lbl%s" Text="{res:Loc prop.detail.%s}" Grid.Row="%d" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>'''
            % (name, key, row))

def box(name, key, row, multiline=False):
    extra = ' AcceptsReturn="True" TextWrapping="Wrap" MinHeight="44"' if multiline else ''
    return ('''                        <TextBox x:Name="%sBox" Grid.Row="%d" Grid.Column="1" FontSize="13" Margin="0,2"%s
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 TextChanged="OnPropertyChanged"/>''' % (name, row, extra))

def chk(name, key, row):
    return ('''                        <CheckBox x:Name="%sCheck" Grid.Row="%d" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>''' % (name, row))

def num(name, key, row, val, lo, hi):
    return ('''                        <fa:FANumberBox x:Name="%sBox" Grid.Row="%d" Grid.Column="1"
                                        Value="%s" Minimum="%s" Maximum="%s"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>'''
            % (name, row, val, lo, hi))

def combo(name, key, row, items):
    body = '\n'.join('                            <ComboBoxItem Content="{res:Loc prop.detail.%s}" Tag="%s"/>' % (k, t)
                     for k, t in items)
    return ('''                        <ComboBox x:Name="%sCombo" Grid.Row="%d" Grid.Column="1"
                                  Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                  BorderBrush="{StaticResource HmiInputBorderBrush}" FontSize="13"
                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
%s
                        </ComboBox>''' % (name, row, body))

rowsxml = []
rowsxml.append(lbl('ChartKind', 'chart.kind', 52))
rowsxml.append(combo('ChartKind', 'chart.kind', 52, [('chart.kind.bar', 'bar'), ('chart.kind.pie', 'pie'), ('chart.kind.donut', 'donut')]))
rowsxml.append(lbl('ChartSeries', 'chart.series', 53))
rowsxml.append(box('ChartSeries', 'chart.series', 53, True))
rowsxml.append(lbl('ChartLegend', 'chart.legend', 54))
rowsxml.append(chk('ChartLegend', 'chart.legend', 54))
rowsxml.append(lbl('ChartValue', 'chart.value', 55))
rowsxml.append(chk('ChartValue', 'chart.value', 55))
rowsxml.append(lbl('XyTagX', 'xy.tagx', 56))
rowsxml.append(box('XyTagX', 'xy.tagx', 56))
rowsxml.append(lbl('XyTagY', 'xy.tagy', 57))
rowsxml.append(box('XyTagY', 'xy.tagy', 57))
rowsxml.append(lbl('XyDot', 'xy.dot', 58))
rowsxml.append(chk('XyDot', 'xy.dot', 58))
rowsxml.append(lbl('XyLine', 'xy.line', 59))
rowsxml.append(chk('XyLine', 'xy.line', 59))
rowsxml.append(lbl('HistColumns', 'histtable.columns', 60))
rowsxml.append(box('HistColumns', 'histtable.columns', 60, True))
rowsxml.append(lbl('HistGroup', 'histtable.group', 61))
rowsxml.append(box('HistGroup', 'histtable.group', 61))
rowsxml.append(lbl('HistRowHeight', 'histtable.rowheight', 62))
rowsxml.append(num('HistRowHeight', 'histtable.rowheight', 62, '0', '0', '60'))
rowsxml.append(lbl('HistScrollbar', 'histtable.scrollbar', 63))
rowsxml.append(chk('HistScrollbar', 'histtable.scrollbar', 63))
rowsxml.append(lbl('RecipeColumns', 'recipe.columns', 64))
rowsxml.append(box('RecipeColumns', 'recipe.columns', 64, True))
rowsxml.append(lbl('RecipeGroup', 'recipe.group', 65))
rowsxml.append(box('RecipeGroup', 'recipe.group', 65))
rowsxml.append(lbl('RecipeUnit', 'recipe.unit', 66))
rowsxml.append(chk('RecipeUnit', 'recipe.unit', 66))
rowsxml.append(lbl('AsciiEncoding', 'ascii.encoding', 67))
rowsxml.append(combo('AsciiEncoding', 'ascii.encoding', 67, [('ascii.encoding.gbk', 'gbk'), ('ascii.encoding.ascii', 'ascii')]))
rowsxml.append(lbl('AsciiMaxLen', 'ascii.maxlen', 68))
rowsxml.append(num('AsciiMaxLen', 'ascii.maxlen', 68, '32', '1', '64'))

block = '\n'.join(rowsxml)

# ---------- XAML ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()
old = ','.join(['Auto'] * 52)
assert old in s, 'grid rows 52 not found'
s = s.replace(old, ','.join(['Auto'] * rows), 1)

tail = '''                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
                            <ComboBoxItem Content="{res:Loc prop.shape.kind.wedge}" Tag="wedge"/>
                        </ComboBox>
                    </Grid>'''
add = '''                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
                            <ComboBoxItem Content="{res:Loc prop.shape.kind.wedge}" Tag="wedge"/>
                        </ComboBox>
                        <!-- 批次 5：图表 / XY 曲线 / 历史表 / 配方表 / ASCII 显示 -->
%s
                    </Grid>''' % block
if 'ChartKindCombo' not in s:
    s = s.replace(tail, add, 1)
open(p, 'w', encoding='utf-8', newline='').write(s)
print('xaml ok')

# ---------- Parameters.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Parameters.cs'
s = open(p, encoding='utf-8', newline='').read()
miss = []


def rep(a, b):
    global s
    if a not in s:
        miss.append(a[:46])
        return
    s = s.replace(a, b, 1)


pairs = [
    ('ChartKind', 'ChartKind'), ('ChartSeries', 'ChartSeries'), ('ChartLegend', 'ChartLegend'),
    ('ChartValue', 'ChartShowValue'), ('XyTagX', 'XyTagX'), ('XyTagY', 'XyTagY'),
    ('XyDot', 'XyDot'), ('XyLine', 'XyLine'), ('HistColumns', 'HistTableColumns'),
    ('HistGroup', 'HistTableGroup'), ('HistRowHeight', 'HistTableRowHeight'),
    ('HistScrollbar', 'HistTableScrollbar'), ('RecipeColumns', 'RecipeColumns'),
    ('RecipeGroup', 'RecipeGroup'), ('RecipeUnit', 'RecipeShowUnit'),
    ('AsciiEncoding', 'AsciiEncoding'), ('AsciiMaxLen', 'AsciiMaxLen'),
]
hide = '\n'.join('        SetParamRow(Lbl%s, %sBox, false);' % (n, n) if not n.startswith(('ChartLegend', 'ChartValue', 'XyDot', 'XyLine', 'HistScrollbar', 'RecipeUnit'))
                 else '        SetParamRow(Lbl%s, %sCheck, false);' % (n, n)
                 for n, _ in pairs)
# 两个下拉单独处理
hide = hide.replace('        SetParamRow(LblChartKind, ChartKindBox, false);', '        SetParamRow(LblChartKind, ChartKindCombo, false);')
hide = hide.replace('        SetParamRow(LblAsciiEncoding, AsciiEncodingBox, false);', '        SetParamRow(LblAsciiEncoding, AsciiEncodingCombo, false);')
rep('        SetParamRow(LblShapeKind, ShapeKindCombo, false);',
    '        SetParamRow(LblShapeKind, ShapeKindCombo, false);\n        // 批次 5\n' + hide)

show = '''            case HmiControlType.Chart:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblChartKind, ChartKindCombo, true);
                SetParamRow(LblChartSeries, ChartSeriesBox, true);
                SetParamRow(LblChartLegend, ChartLegendCheck, true);
                SetParamRow(LblChartValue, ChartValueCheck, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.XyCurve:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblXyTagX, XyTagXBox, true);
                SetParamRow(LblXyTagY, XyTagYBox, true);
                SetParamRow(LblXyDot, XyDotCheck, true);
                SetParamRow(LblXyLine, XyLineCheck, true);
                ShowCurveRows(true);
                break;
            case HmiControlType.HistoryTable:
                SetParamRow(LblHistColumns, HistColumnsBox, true);
                SetParamRow(LblHistGroup, HistGroupBox, true);
                SetParamRow(LblHistRowHeight, HistRowHeightBox, true);
                SetParamRow(LblHistScrollbar, HistScrollbarCheck, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.RecipeTable:
                SetParamRow(LblRecipeColumns, RecipeColumnsBox, true);
                SetParamRow(LblRecipeGroup, RecipeGroupBox, true);
                SetParamRow(LblRecipeUnit, RecipeUnitCheck, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.AsciiDisplay:
                SetParamRow(LblAsciiEncoding, AsciiEncodingCombo, true);
                SetParamRow(LblAsciiMaxLen, AsciiMaxLenBox, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Shape:'''
rep('            case HmiControlType.Shape:', show)

fill = '''        ChartSeriesBox.Text = control.ChartSeries ?? "";
        ChartLegendCheck.IsChecked = control.ChartShowLegend;
        ChartValueCheck.IsChecked = control.ChartShowValue;
        SelectComboByTag(ChartKindCombo, control.ChartKind, 0);
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
'''
rep('        SelectComboByTag(ShapeKindCombo, control.ShapeKind, 0);',
    '        SelectComboByTag(ShapeKindCombo, control.ShapeKind, 0);\n' + fill)
open(p, 'w', encoding='utf-8', newline='').write(s)
print('params MISS:', miss)

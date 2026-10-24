rows = 69
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

def num(name, row, val, lo, hi):
    return ('''                        <fa:FANumberBox x:Name="%sBox" Grid.Row="%d" Grid.Column="1"
                                        Value="%s" Minimum="%s" Maximum="%s"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>'''
            % (name, row, val, lo, hi))

def combo(name, row, items):
    body = '\n'.join('                            <ComboBoxItem Content="{res:Loc prop.detail.%s}" Tag="%s"/>' % (k, t)
                     for k, t in items)
    return ('''                        <ComboBox x:Name="%sCombo" Grid.Row="%d" Grid.Column="1"
                                  Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                  BorderBrush="{StaticResource HmiInputBorderBrush}" FontSize="13"
                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
%s
                        </ComboBox>''' % (name, row, body))

parts = [
    lbl('ChartKind', 'chart.kind', 52), combo('ChartKind', 52, [('chart.kind.bar', 'bar'), ('chart.kind.pie', 'pie'), ('chart.kind.donut', 'donut')]),
    lbl('ChartSeries', 'chart.series', 53), box('ChartSeries', 'chart.series', 53, True),
    lbl('ChartLegend', 'chart.legend', 54), chk('ChartLegend', 'chart.legend', 54),
    lbl('ChartValue', 'chart.value', 55), chk('ChartValue', 'chart.value', 55),
    lbl('XyTagX', 'xy.tagx', 56), box('XyTagX', 'xy.tagx', 56),
    lbl('XyTagY', 'xy.tagy', 57), box('XyTagY', 'xy.tagy', 57),
    lbl('XyDot', 'xy.dot', 58), chk('XyDot', 'xy.dot', 58),
    lbl('XyLine', 'xy.line', 59), chk('XyLine', 'xy.line', 59),
    lbl('HistColumns', 'histtable.columns', 60), box('HistColumns', 'histtable.columns', 60, True),
    lbl('HistGroup', 'histtable.group', 61), box('HistGroup', 'histtable.group', 61),
    lbl('HistRowHeight', 'histtable.rowheight', 62), num('HistRowHeight', 62, '0', '0', '60'),
    lbl('HistScrollbar', 'histtable.scrollbar', 63), chk('HistScrollbar', 'histtable.scrollbar', 63),
    lbl('RecipeColumns', 'recipe.columns', 64), box('RecipeColumns', 'recipe.columns', 64, True),
    lbl('RecipeGroup', 'recipe.group', 65), box('RecipeGroup', 'recipe.group', 65),
    lbl('RecipeUnit', 'recipe.unit', 66), chk('RecipeUnit', 'recipe.unit', 66),
    lbl('AsciiEncoding', 'ascii.encoding', 67), combo('AsciiEncoding', 67, [('ascii.encoding.gbk', 'gbk'), ('ascii.encoding.ascii', 'ascii')]),
    lbl('AsciiMaxLen', 'ascii.maxlen', 68), num('AsciiMaxLen', 68, '32', '1', '64'),
]
block = '\n'.join(parts)

p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()

if 'ChartKindCombo' not in s:
    # 找 ShapeKindCombo 的结束标签（它后面紧跟的就是那个 Grid 的结尾）
    i = s.index('x:Name="ShapeKindCombo"')
    j = s.index('</ComboBox>', i) + len('</ComboBox>')
    s = s[:j] + '\n' + block + s[j:]

open(p, 'w', encoding='utf-8', newline='').write(s)
print('inserted:', 'ChartKindCombo' in s)
print('rows def count:', s.count(','.join(['Auto'] * rows)))

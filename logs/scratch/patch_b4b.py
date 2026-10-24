KINDS = {
    "arc": ("弧", "Arc"), "arrow": ("箭头", "Arrow"), "arrow_thick": ("粗箭头", "Thick arrow"),
    "arrow_thin": ("细箭头", "Thin arrow"), "ball3d": ("3D 球", "3D ball"), "callout": ("标注框", "Callout"),
    "circle": ("圆", "Circle"), "cone3d": ("3D 锥", "3D cone"), "container": ("容器", "Container"),
    "cross": ("十字", "Cross"), "cube": ("立方体", "Cube"), "diamond": ("菱形", "Diamond"),
    "ellipse": ("椭圆", "Ellipse"), "hexagon": ("六边形", "Hexagon"), "line": ("直线", "Line"),
    "octagon": ("八边形", "Octagon"), "parallelogram": ("平行四边形", "Parallelogram"),
    "pie": ("饼图", "Pie"), "pipe_elbow": ("弯管", "Pipe elbow"), "pipe_h": ("横管", "Pipe (horizontal)"),
    "pipe_joint": ("管接头", "Pipe joint"), "pipe_v": ("竖管", "Pipe (vertical)"),
    "plane_concave": ("凹面", "Concave plane"), "plane_convex": ("凸面", "Convex plane"),
    "plane_groove": ("槽面", "Groove plane"), "polygon": ("多边形", "Polygon"),
    "polyline": ("折线", "Polyline"), "rect": ("矩形", "Rectangle"),
    "right_triangle": ("直角三角形", "Right triangle"), "round_rect": ("圆角矩形", "Round rectangle"),
    "star": ("星形", "Star"), "star5": ("五角星", "Five-point star"), "tank": ("罐", "Tank"),
    "torus3d": ("3D 环", "3D torus"), "trapezoid": ("梯形", "Trapezoid"),
    "triangle": ("三角形", "Triangle"), "valve_ball": ("球阀", "Ball valve"),
    "valve_butterfly": ("蝶阀", "Butterfly valve"), "valve_gate": ("闸阀", "Gate valve"),
    "wedge": ("楔形", "Wedge"),
}

# ---------- XAML：多余一行 + 下拉（40 项） ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()
old = ','.join(['Auto'] * 51)
assert old in s, 'grid rows 51 not found'
s = s.replace(old, ','.join(['Auto'] * 52), 1)

items = '\n'.join(
    '                            <ComboBoxItem Content="{res:Loc prop.shape.kind.%s}" Tag="%s"/>' % (k, k)
    for k in KINDS)
tail = '''                        <TextBox x:Name="PipeChannelColorBox" Grid.Row="50" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 PlaceholderText="{res:Loc prop.color.hint}" TextChanged="OnPropertyChanged"/>
                    </Grid>'''
add = '''                        <TextBox x:Name="PipeChannelColorBox" Grid.Row="50" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 PlaceholderText="{res:Loc prop.color.hint}" TextChanged="OnPropertyChanged"/>
                        <!-- 通用图元：kind 下拉（契约 figures 的 40 种） -->
                        <TextBlock x:Name="LblShapeKind" Text="{res:Loc prop.detail.shape.kind}" Grid.Row="51" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <ComboBox x:Name="ShapeKindCombo" Grid.Row="51" Grid.Column="1"
                                  Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                  BorderBrush="{StaticResource HmiInputBorderBrush}" FontSize="13"
                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
%s
                        </ComboBox>
                    </Grid>''' % items
if 'ShapeKindCombo' not in s:
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
        miss.append(a[:50])
        return
    s = s.replace(a, b, 1)


rep('        SetParamRow(LblPipeChannel, PipeChannelColorBox, false);',
    '''        SetParamRow(LblPipeChannel, PipeChannelColorBox, false);
        SetParamRow(LblShapeKind, ShapeKindCombo, false);''')

rep('''            case HmiControlType.Pipe:''',
    '''            case HmiControlType.Shape:
                SetParamRow(LblShapeKind, ShapeKindCombo, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Pipe:''')

rep('        PipeChannelColorBox.Text = control.PipeChannelColor ?? "";',
    '''        PipeChannelColorBox.Text = control.PipeChannelColor ?? "";
        SelectComboByTag(ShapeKindCombo, control.ShapeKind, 0);''')
open(p, 'w', encoding='utf-8', newline='').write(s)
print('params MISS:', miss)

# ---------- Changes.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''            if (PipeChannelColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeChannelColor), string.IsNullOrWhiteSpace(PipeChannelColorBox.Text) ? null : PipeChannelColorBox.Text.Trim(), v => _currentControl.PipeChannelColor = v);'''
b = a + '''
            if (ShapeKindCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ShapeKind), GetComboTag(ShapeKindCombo), v => _currentControl.ShapeKind = v);'''
if 'ShapeKindCombo.IsVisible' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 字典 ----------
zh_items = '\n'.join('  "prop.shape.kind.%s": "%s",' % (k, v[0]) for k, v in KINDS.items())
en_items = '\n'.join('  "prop.shape.kind.%s": "%s",' % (k, v[1]) for k, v in KINDS.items())

zh = '  "tb.shape": "图元",\n  "prop.detail.shape.kind": "种类",\n' + zh_items + '\n'
en = '  "tb.shape": "Shape",\n  "prop.detail.shape.kind": "Kind",\n' + en_items + '\n'

for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'tb.shape' in s:
        print('skip', p)
        continue
    anchor = '  "tb.frameanim"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
    print('strings ok', p)

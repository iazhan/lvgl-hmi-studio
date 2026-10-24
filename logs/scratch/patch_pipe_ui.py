import re

# ---------- XAML：扩到 45 行 + 两个新行 ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()
old = ','.join(['Auto'] * 43)
assert old in s, 'grid rows 43 not found'
s = s.replace(old, ','.join(['Auto'] * 45), 1)

tail = '''                        <CheckBox x:Name="AlarmShowTimeCheck" Grid.Row="48" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>
                    </Grid>'''
add = '''                        <CheckBox x:Name="AlarmShowTimeCheck" Grid.Row="48" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>
                        <!-- 管道（控件 pipe）：管壁厚度与通道色 -->
                        <TextBlock x:Name="LblPipeWall" Text="{res:Loc prop.detail.pipe.wall}" Grid.Row="49" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="PipeWallBox" Grid.Row="49" Grid.Column="1"
                                        Value="0.2" Minimum="0.05" Maximum="0.45" SmallChange="0.05"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <TextBlock x:Name="LblPipeChannel" Text="{res:Loc prop.detail.pipe.channel}" Grid.Row="50" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <TextBox x:Name="PipeChannelColorBox" Grid.Row="50" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 PlaceholderText="{res:Loc prop.color.hint}" TextChanged="OnPropertyChanged"/>
                    </Grid>'''
if 'PipeWallBox' not in s:
    s = s.replace(tail, add, 1)
# 行数要够 51 行（0..50）—— 上面替换漏算了，改成 51
s = s.replace(','.join(['Auto'] * 45), ','.join(['Auto'] * 51), 1)
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


rep('        SetParamRow(LblPipeFlowSpeed, PipeFlowSpeedBox, false);',
    '''        SetParamRow(LblPipeFlowSpeed, PipeFlowSpeedBox, false);
        SetParamRow(LblPipeWall, PipeWallBox, false);
        SetParamRow(LblPipeChannel, PipeChannelColorBox, false);''')

rep('                SetParamRow(LblPipeFlowSpeed, PipeFlowSpeedBox, true);',
    '''                SetParamRow(LblPipeFlowSpeed, PipeFlowSpeedBox, true);
                SetParamRow(LblPipeWall, PipeWallBox, true);
                SetParamRow(LblPipeChannel, PipeChannelColorBox, true);
                DetailParamExpander.IsVisible = true;''')

rep('        FillAlarmFields(control);',
    '''        FillAlarmFields(control);
        PipeWallBox.Value = (decimal)control.PipeWall;
        PipeChannelColorBox.Text = control.PipeChannelColor ?? "";''')
open(p, 'w', encoding='utf-8', newline='').write(s)
print('params MISS:', miss)

# ---------- Changes.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''            if (AlarmShowTimeCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmShowTime), AlarmShowTimeCheck.IsChecked == true, v => _currentControl.AlarmShowTime = v);'''
b = a + '''
            if (PipeWallBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeWall), (double)PipeWallBox.Value, v => _currentControl.PipeWall = v);
            if (PipeChannelColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeChannelColor), string.IsNullOrWhiteSpace(PipeChannelColorBox.Text) ? null : PipeChannelColorBox.Text.Trim(), v => _currentControl.PipeChannelColor = v);'''
if 'PipeWallBox.IsVisible' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 挂接 ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '        AlarmScrollCharsBox.ValueChanged += OnNumberChanged;'
b = a + '\n        PipeWallBox.ValueChanged += OnNumberChanged;'
if 'PipeWallBox.ValueChanged' not in s:
    s = s.replace(a, b, 1)
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 字典 ----------
zh = '''  "prop.detail.pipe.wall": "管壁厚度比例",
  "prop.detail.pipe.channel": "通道底色（空=自动）",
'''
en = '''  "prop.detail.pipe.wall": "Wall thickness ratio",
  "prop.detail.pipe.channel": "Channel color (empty = auto)",
'''
for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'prop.detail.pipe.wall' in s:
        continue
    anchor = '  "prop.detail.alarmbar.showtime"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
print('strings ok')

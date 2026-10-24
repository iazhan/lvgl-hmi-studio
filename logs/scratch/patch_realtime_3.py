rows43 = ','.join(['Auto'] * 43)

# ---------- XAML：扩行 + 两个新行 ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()
old = ','.join(['Auto'] * 41)
assert old in s, 'grid rows not found'
s = s.replace(old, rows43, 1)

tail = '''                        <TextBox x:Name="AlarmTimeFormatBox" Grid.Row="40" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 TextChanged="OnPropertyChanged"/>
                    </Grid>'''
add = '''                        <TextBox x:Name="AlarmTimeFormatBox" Grid.Row="40" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 TextChanged="OnPropertyChanged"/>
                        <!-- 批次 2b：实时曲线的采样参数 -->
                        <TextBlock x:Name="LblSampleMs" Text="{res:Loc prop.detail.sample}" Grid.Row="41" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="SampleMsBox" Grid.Row="41" Grid.Column="1"
                                        Value="1000" Minimum="100" Maximum="60000"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <TextBlock x:Name="LblHistoryS" Text="{res:Loc prop.detail.history}" Grid.Row="42" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="HistorySBox" Grid.Row="42" Grid.Column="1"
                                        Value="0" Minimum="0" Maximum="86400"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                    </Grid>'''
if 'SampleMsBox' not in s:
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


rep('''        SetParamRow(LblAlarmTimeFormat, AlarmTimeFormatBox, false);''',
    '''        SetParamRow(LblAlarmTimeFormat, AlarmTimeFormatBox, false);
        // 批次 2b：实时曲线采样参数
        SetParamRow(LblSampleMs, SampleMsBox, false);
        SetParamRow(LblHistoryS, HistorySBox, false);''')

# realtime 分支：放在 HistoryCurve 分支之后
rep('''            case HmiControlType.HistoryCurve:
                SetParamRow(LblCgTrendGroup, CgTrendGroupBox, true);''',
    '''            case HmiControlType.RealtimeCurve:
                SetParamRow(LblCgSpanMin, CgSpanMinBox, true);
                SetParamRow(LblCgCurveVars, CgCurveVarsBox, true);
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblSampleMs, SampleMsBox, true);
                SetParamRow(LblHistoryS, HistorySBox, true);
                ShowCurveRows(true);
                break;
            case HmiControlType.HistoryCurve:
                SetParamRow(LblCgTrendGroup, CgTrendGroupBox, true);''')

rep('''        AlarmTimeFormatBox.Text = control.AlarmTimeFormat ?? "";''',
    '''        AlarmTimeFormatBox.Text = control.AlarmTimeFormat ?? "";
        SampleMsBox.Value = control.SampleMs;
        HistorySBox.Value = control.HistoryS;''')
open(p, 'w', encoding='utf-8', newline='').write(s)
print('params MISS:', miss)

# ---------- Changes.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''            if (AlarmTimeFormatBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmTimeFormat), string.IsNullOrWhiteSpace(AlarmTimeFormatBox.Text) ? null : AlarmTimeFormatBox.Text.Trim(), v => _currentControl.AlarmTimeFormat = v);'''
b = a + '''
            // 批次 2b：实时曲线采样参数
            if (SampleMsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.SampleMs), (int)SampleMsBox.Value, v => _currentControl.SampleMs = v);
            if (HistorySBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HistoryS), (int)HistorySBox.Value, v => _currentControl.HistoryS = v);'''
if 'SampleMsBox.IsVisible' not in s:
    s = s.replace(a, b, 1)
    print('changes ok')
else:
    print('changes already')
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 挂接 ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''        AlarmBlinkMsBox.ValueChanged += OnNumberChanged;'''
b = a + '''
        SampleMsBox.ValueChanged += OnNumberChanged;
        HistorySBox.ValueChanged += OnNumberChanged;'''
if 'SampleMsBox.ValueChanged' not in s:
    s = s.replace(a, b, 1)
    print('wiring ok')
else:
    print('wiring already')
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 字典 ----------
zh = '''  "tb.realtimecurve": "实时曲线",
  "prop.detail.sample": "采样周期（ms）",
  "prop.detail.history": "保留历史（秒）",
'''
en = '''  "tb.realtimecurve": "Realtime curve",
  "prop.detail.sample": "Sample period (ms)",
  "prop.detail.history": "Keep history (s)",
'''
for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'tb.realtimecurve' in s:
        print('strings skip', p)
        continue
    anchor = '  "tb.historycurve"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
    print('strings ok', p)

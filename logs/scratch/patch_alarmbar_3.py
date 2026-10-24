rows49 = ','.join(['Auto'] * 49)

# ---------- XAML ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()
old = ','.join(['Auto'] * 43)
assert old in s, 'grid rows 43 not found'
s = s.replace(old, rows49, 1)

tail = '''                        <fa:FANumberBox x:Name="HistorySBox" Grid.Row="42" Grid.Column="1"
                                        Value="0" Minimum="0" Maximum="86400"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                    </Grid>'''
add = '''                        <fa:FANumberBox x:Name="HistorySBox" Grid.Row="42" Grid.Column="1"
                                        Value="0" Minimum="0" Maximum="86400"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <!-- 批次 2d：报警条 -->
                        <TextBlock x:Name="LblAlarmNormalText" Text="{res:Loc prop.detail.alarmbar.normal}" Grid.Row="43" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <TextBox x:Name="AlarmNormalTextBox" Grid.Row="43" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 TextChanged="OnPropertyChanged"/>
                        <TextBlock x:Name="LblAlarmOnlyUnacked" Text="{res:Loc prop.detail.alarmbar.onlyunacked}" Grid.Row="44" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <CheckBox x:Name="AlarmOnlyUnackedCheck" Grid.Row="44" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>
                        <TextBlock x:Name="LblAlarmScrollMs" Text="{res:Loc prop.detail.alarmbar.scrollms}" Grid.Row="45" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="AlarmScrollMsBox" Grid.Row="45" Grid.Column="1"
                                        Value="3000" Minimum="500" Maximum="60000"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <TextBlock x:Name="LblAlarmScrollChars" Text="{res:Loc prop.detail.alarmbar.scrollchars}" Grid.Row="46" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="AlarmScrollCharsBox" Grid.Row="46" Grid.Column="1"
                                        Value="0" Minimum="0" Maximum="64"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <TextBlock x:Name="LblAlarmShowDate" Text="{res:Loc prop.detail.alarmbar.showdate}" Grid.Row="47" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <CheckBox x:Name="AlarmShowDateCheck" Grid.Row="47" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>
                        <TextBlock x:Name="LblAlarmShowTime" Text="{res:Loc prop.detail.alarmbar.showtime}" Grid.Row="48" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <CheckBox x:Name="AlarmShowTimeCheck" Grid.Row="48" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>
                    </Grid>'''
if 'AlarmNormalTextBox' not in s:
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


rep('''        SetParamRow(LblHistoryS, HistorySBox, false);''',
    '''        SetParamRow(LblHistoryS, HistorySBox, false);
        // 批次 2d：报警条
        SetParamRow(LblAlarmNormalText, AlarmNormalTextBox, false);
        SetParamRow(LblAlarmOnlyUnacked, AlarmOnlyUnackedCheck, false);
        SetParamRow(LblAlarmScrollMs, AlarmScrollMsBox, false);
        SetParamRow(LblAlarmScrollChars, AlarmScrollCharsBox, false);
        SetParamRow(LblAlarmShowDate, AlarmShowDateCheck, false);
        SetParamRow(LblAlarmShowTime, AlarmShowTimeCheck, false);''')

rep('''            case HmiControlType.AlarmList:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);''',
    '''            case HmiControlType.AlarmBar:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);
                SetParamRow(LblAlarmNormalText, AlarmNormalTextBox, true);
                SetParamRow(LblAlarmOnlyUnacked, AlarmOnlyUnackedCheck, true);
                SetParamRow(LblAlarmScrollMs, AlarmScrollMsBox, true);
                SetParamRow(LblAlarmScrollChars, AlarmScrollCharsBox, true);
                SetParamRow(LblAlarmShowDate, AlarmShowDateCheck, true);
                SetParamRow(LblAlarmShowTime, AlarmShowTimeCheck, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.AlarmList:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);''')

rep('''        HistorySBox.Value = control.HistoryS;''',
    '''        HistorySBox.Value = control.HistoryS;
        AlarmNormalTextBox.Text = control.AlarmNormalText ?? "";
        AlarmOnlyUnackedCheck.IsChecked = control.AlarmOnlyUnacked;
        AlarmScrollMsBox.Value = control.AlarmScrollMs;
        AlarmScrollCharsBox.Value = control.AlarmScrollChars;
        AlarmShowDateCheck.IsChecked = control.AlarmShowDate;
        AlarmShowTimeCheck.IsChecked = control.AlarmShowTime;''')
open(p, 'w', encoding='utf-8', newline='').write(s)
print('params MISS:', miss)

# ---------- Changes.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''            if (HistorySBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HistoryS), (int)HistorySBox.Value, v => _currentControl.HistoryS = v);'''
b = a + '''
            // 批次 2d：报警条
            if (AlarmNormalTextBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmNormalText), string.IsNullOrWhiteSpace(AlarmNormalTextBox.Text) ? null : AlarmNormalTextBox.Text.Trim(), v => _currentControl.AlarmNormalText = v);
            if (AlarmOnlyUnackedCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmOnlyUnacked), AlarmOnlyUnackedCheck.IsChecked == true, v => _currentControl.AlarmOnlyUnacked = v);
            if (AlarmScrollMsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmScrollMs), (int)AlarmScrollMsBox.Value, v => _currentControl.AlarmScrollMs = v);
            if (AlarmScrollCharsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmScrollChars), (int)AlarmScrollCharsBox.Value, v => _currentControl.AlarmScrollChars = v);
            if (AlarmShowDateCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmShowDate), AlarmShowDateCheck.IsChecked == true, v => _currentControl.AlarmShowDate = v);
            if (AlarmShowTimeCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmShowTime), AlarmShowTimeCheck.IsChecked == true, v => _currentControl.AlarmShowTime = v);'''
if 'AlarmNormalTextBox.IsVisible' not in s:
    s = s.replace(a, b, 1)
    print('changes ok')
else:
    print('changes already')
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 挂接 ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''        HistorySBox.ValueChanged += OnNumberChanged;'''
b = a + '''
        AlarmScrollMsBox.ValueChanged += OnNumberChanged;
        AlarmScrollCharsBox.ValueChanged += OnNumberChanged;'''
if 'AlarmScrollMsBox.ValueChanged' not in s:
    s = s.replace(a, b, 1)
    print('wiring ok')
else:
    print('wiring already')
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 字典 ----------
zh = '''  "tb.alarmbar": "报警条",
  "prop.detail.alarmbar.normal": "无报警时文字",
  "prop.detail.alarmbar.onlyunacked": "只看未确认",
  "prop.detail.alarmbar.scrollms": "滚动间隔（ms）",
  "prop.detail.alarmbar.scrollchars": "滚动步长（字符，0=不滚）",
  "prop.detail.alarmbar.showdate": "显示日期",
  "prop.detail.alarmbar.showtime": "显示时间",
'''
en = '''  "tb.alarmbar": "Alarm bar",
  "prop.detail.alarmbar.normal": "Text when no alarm",
  "prop.detail.alarmbar.onlyunacked": "Unacknowledged only",
  "prop.detail.alarmbar.scrollms": "Scroll interval (ms)",
  "prop.detail.alarmbar.scrollchars": "Scroll step (chars, 0 = none)",
  "prop.detail.alarmbar.showdate": "Show date",
  "prop.detail.alarmbar.showtime": "Show time",
'''
for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'tb.alarmbar' in s:
        print('strings skip', p)
        continue
    anchor = '  "tb.realtimecurve"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
    print('strings ok', p)

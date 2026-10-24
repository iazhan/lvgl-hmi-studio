rows41 = ','.join(['Auto'] * 41)

# ---------- XAML ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml'
s = open(p, encoding='utf-8', newline='').read()

old_grid = 'Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto,Auto'
assert old_grid in s, 'grid row def not found'
s = s.replace(old_grid, rows41, 1)

anchor = '''                        <ComboBox x:Name="TabPosCombo" Grid.Row="30" Grid.Column="1"'''
# 在 TabPosCombo 的 </ComboBox> 之后插入 10 行
tail = '''                            <ComboBoxItem Content="{res:Loc prop.detail.tab.pos.right}" Tag="Right"/>
                        </ComboBox>
                    </Grid>'''
add = '''                            <ComboBoxItem Content="{res:Loc prop.detail.tab.pos.right}" Tag="Right"/>
                        </ComboBox>
                        <!-- 批次 2c：报警列表显示细节 -->
                        <TextBlock x:Name="LblAlarmOrder" Text="{res:Loc prop.detail.alarm.order}" Grid.Row="31" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <ComboBox x:Name="AlarmOrderCombo" Grid.Row="31" Grid.Column="1"
                                  Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                  BorderBrush="{StaticResource HmiInputBorderBrush}" FontSize="13"
                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.order.newest}" Tag="newest_first"/>
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.order.oldest}" Tag="newest_last"/>
                        </ComboBox>
                        <TextBlock x:Name="LblAlarmAckOn" Text="{res:Loc prop.detail.alarm.ack}" Grid.Row="32" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <ComboBox x:Name="AlarmAckOnCombo" Grid.Row="32" Grid.Column="1"
                                  Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                  BorderBrush="{StaticResource HmiInputBorderBrush}" FontSize="13"
                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.ack.none}" Tag="none"/>
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.ack.click}" Tag="click"/>
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.ack.dblclick}" Tag="dblclick"/>
                        </ComboBox>
                        <TextBlock x:Name="LblAlarmGrid" Text="{res:Loc prop.detail.alarm.grid}" Grid.Row="33" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <ComboBox x:Name="AlarmGridCombo" Grid.Row="33" Grid.Column="1"
                                  Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                  BorderBrush="{StaticResource HmiInputBorderBrush}" FontSize="13"
                                  SelectedIndex="0" IsVisible="False" SelectionChanged="OnPropertyChanged">
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.grid.all}" Tag="all"/>
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.grid.rows}" Tag="rows"/>
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.grid.border}" Tag="border"/>
                            <ComboBoxItem Content="{res:Loc prop.detail.alarm.grid.none}" Tag="none"/>
                        </ComboBox>
                        <TextBlock x:Name="LblAlarmRowHeight" Text="{res:Loc prop.detail.alarm.rowheight}" Grid.Row="34" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="AlarmRowHeightBox" Grid.Row="34" Grid.Column="1"
                                        Value="0" Minimum="0" Maximum="60"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <TextBlock x:Name="LblAlarmScrollbar" Text="{res:Loc prop.detail.alarm.scrollbar}" Grid.Row="35" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <CheckBox x:Name="AlarmScrollbarCheck" Grid.Row="35" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>
                        <TextBlock x:Name="LblAlarmRefresh" Text="{res:Loc prop.detail.alarm.refresh}" Grid.Row="36" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="AlarmRefreshBox" Grid.Row="36" Grid.Column="1"
                                        Value="500" Minimum="0" Maximum="60000"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <TextBlock x:Name="LblAlarmBlink" Text="{res:Loc prop.detail.alarm.blink}" Grid.Row="37" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <CheckBox x:Name="AlarmBlinkCheck" Grid.Row="37" Grid.Column="1"
                                  Foreground="{StaticResource HmiTextPrimaryBrush}" FontSize="13"
                                  VerticalAlignment="Center" Margin="0,2" IsVisible="False"
                                  IsCheckedChanged="OnPropertyChanged"/>
                        <TextBlock x:Name="LblAlarmBlinkMs" Text="{res:Loc prop.detail.alarm.blinkms}" Grid.Row="38" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <fa:FANumberBox x:Name="AlarmBlinkMsBox" Grid.Row="38" Grid.Column="1"
                                        Value="600" Minimum="100" Maximum="5000"
                                        SpinButtonPlacementMode="Compact" Margin="0,2" IsVisible="False"/>
                        <TextBlock x:Name="LblAlarmDateFormat" Text="{res:Loc prop.detail.alarm.datefmt}" Grid.Row="39" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <TextBox x:Name="AlarmDateFormatBox" Grid.Row="39" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 TextChanged="OnPropertyChanged"/>
                        <TextBlock x:Name="LblAlarmTimeFormat" Text="{res:Loc prop.detail.alarm.timefmt}" Grid.Row="40" Grid.Column="0"
                                   Foreground="{StaticResource HmiTextPrimaryBrush}" VerticalAlignment="Center" IsVisible="False"/>
                        <TextBox x:Name="AlarmTimeFormatBox" Grid.Row="40" Grid.Column="1" FontSize="13" Margin="0,2"
                                 Background="{StaticResource HmiBgPanelBrush}" Foreground="{StaticResource HmiTextPrimaryBrush}"
                                 BorderBrush="{StaticResource HmiInputBorderBrush}" IsVisible="False"
                                 TextChanged="OnPropertyChanged"/>
                    </Grid>'''
if 'AlarmOrderCombo' not in s:
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


rep('''        ShowCurveRows(false);''', '''        ShowCurveRows(false);
        // 批次 2c：报警列表显示细节
        SetParamRow(LblAlarmOrder, AlarmOrderCombo, false);
        SetParamRow(LblAlarmAckOn, AlarmAckOnCombo, false);
        SetParamRow(LblAlarmGrid, AlarmGridCombo, false);
        SetParamRow(LblAlarmRowHeight, AlarmRowHeightBox, false);
        SetParamRow(LblAlarmScrollbar, AlarmScrollbarCheck, false);
        SetParamRow(LblAlarmRefresh, AlarmRefreshBox, false);
        SetParamRow(LblAlarmBlink, AlarmBlinkCheck, false);
        SetParamRow(LblAlarmBlinkMs, AlarmBlinkMsBox, false);
        SetParamRow(LblAlarmDateFormat, AlarmDateFormatBox, false);
        SetParamRow(LblAlarmTimeFormat, AlarmTimeFormatBox, false);''')

rep('''            case HmiControlType.AlarmList:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);
                SetParamRow(LblCgAlarmMode, CgAlarmModeCombo, true);
                SetParamRow(LblCgAlarmRows, CgAlarmRowsBox, true);
                break;''', '''            case HmiControlType.AlarmList:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);
                SetParamRow(LblCgAlarmMode, CgAlarmModeCombo, true);
                SetParamRow(LblCgAlarmRows, CgAlarmRowsBox, true);
                // 批次 2c
                SetParamRow(LblAlarmOrder, AlarmOrderCombo, true);
                SetParamRow(LblAlarmAckOn, AlarmAckOnCombo, true);
                SetParamRow(LblAlarmGrid, AlarmGridCombo, true);
                SetParamRow(LblAlarmRowHeight, AlarmRowHeightBox, true);
                SetParamRow(LblAlarmScrollbar, AlarmScrollbarCheck, true);
                SetParamRow(LblAlarmRefresh, AlarmRefreshBox, true);
                SetParamRow(LblAlarmBlink, AlarmBlinkCheck, true);
                SetParamRow(LblAlarmBlinkMs, AlarmBlinkMsBox, true);
                SetParamRow(LblAlarmDateFormat, AlarmDateFormatBox, true);
                SetParamRow(LblAlarmTimeFormat, AlarmTimeFormatBox, true);
                DetailParamExpander.IsVisible = true;
                break;''')

rep('''        FillCurveFields(control);''', '''        FillCurveFields(control);
        SelectComboByTag(AlarmOrderCombo, control.AlarmOrder, 0);
        SelectComboByTag(AlarmAckOnCombo, control.AlarmAckOn, 1);
        SelectComboByTag(AlarmGridCombo, control.AlarmGrid, 0);
        AlarmRowHeightBox.Value = control.AlarmRowHeight;
        AlarmScrollbarCheck.IsChecked = control.AlarmScrollbar;
        AlarmRefreshBox.Value = control.AlarmRefreshMs;
        AlarmBlinkCheck.IsChecked = control.AlarmBlink;
        AlarmBlinkMsBox.Value = control.AlarmBlinkMs;
        AlarmDateFormatBox.Text = control.AlarmDateFormat ?? "";
        AlarmTimeFormatBox.Text = control.AlarmTimeFormat ?? "";''')
open(p, 'w', encoding='utf-8', newline='').write(s)
print('params MISS:', miss)

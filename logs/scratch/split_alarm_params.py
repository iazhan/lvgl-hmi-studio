p = 'src/HmiEditor.App/Views/PropertyPanelView.Parameters.cs'
s = open(p, encoding='utf-8', newline='').read()

# 1) 隐藏区：报警列表 10 行 → 调用
hide_list_start = s.index('        // 批次 2c：报警列表显示细节\n        SetParamRow(LblAlarmOrder')
hide_list_end = s.index('        SetParamRow(LblAlarmTimeFormat, AlarmTimeFormatBox, false);\n', hide_list_start) + len('        SetParamRow(LblAlarmTimeFormat, AlarmTimeFormatBox, false);\n')
s = s[:hide_list_start] + '        ShowAlarmListRows(false);\n' + s[hide_list_end:]

# 2) 隐藏区：报警条 6 行 → 调用
hide_bar_start = s.index('        // 批次 2d：报警条\n        SetParamRow(LblAlarmNormalText')
hide_bar_end = s.index('        SetParamRow(LblAlarmShowTime, AlarmShowTimeCheck, false);\n', hide_bar_start) + len('        SetParamRow(LblAlarmShowTime, AlarmShowTimeCheck, false);\n')
s = s[:hide_bar_start] + '        ShowAlarmBarRows(false);\n' + s[hide_bar_end:]

# 3) 显示区：报警条分支
bar_start = s.index('            case HmiControlType.AlarmBar:\n')
bar_end = s.index('                DetailParamExpander.IsVisible = true;\n                break;\n', bar_start) + len('                DetailParamExpander.IsVisible = true;\n                break;\n')
s = s[:bar_start] + '''            case HmiControlType.AlarmBar:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);
                ShowAlarmBarRows(true);
                DetailParamExpander.IsVisible = true;
                break;
''' + s[bar_end:]

# 4) 显示区：报警列表分支
list_start = s.index('            case HmiControlType.AlarmList:\n')
list_end = s.index('                DetailParamExpander.IsVisible = true;\n                break;\n', list_start) + len('                DetailParamExpander.IsVisible = true;\n                break;\n')
s = s[:list_start] + '''            case HmiControlType.AlarmList:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);
                SetParamRow(LblCgAlarmMode, CgAlarmModeCombo, true);
                SetParamRow(LblCgAlarmRows, CgAlarmRowsBox, true);
                ShowAlarmListRows(true);
                DetailParamExpander.IsVisible = true;
                break;
''' + s[list_end:]

# 5) 回填 → 调用
fill_start = s.index('        SelectComboByTag(AlarmOrderCombo, control.AlarmOrder, 0);')
fill_end = s.index('        AlarmShowTimeCheck.IsChecked = control.AlarmShowTime;\n', fill_start) + len('        AlarmShowTimeCheck.IsChecked = control.AlarmShowTime;\n')
s = s[:fill_start] + '        FillAlarmFields(control);\n' + s[fill_end:]

open(p, 'w', encoding='utf-8', newline='').write(s)
print('lines now:', s.count('\n') + 1)

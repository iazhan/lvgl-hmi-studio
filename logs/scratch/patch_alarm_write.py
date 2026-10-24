# ---------- Changes.cs ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.Changes.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''            if (CurveLastMinBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveRangeLastMin), (int)CurveLastMinBox.Value, v => _currentControl.CurveRangeLastMin = v);'''
b = a + '''
            // 批次 2c：报警列表显示细节
            if (AlarmOrderCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmOrder), GetComboTag(AlarmOrderCombo), v => _currentControl.AlarmOrder = v);
            if (AlarmAckOnCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmAckOn), GetComboTag(AlarmAckOnCombo), v => _currentControl.AlarmAckOn = v);
            if (AlarmGridCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmGrid), GetComboTag(AlarmGridCombo), v => _currentControl.AlarmGrid = v);
            if (AlarmRowHeightBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmRowHeight), (int)AlarmRowHeightBox.Value, v => _currentControl.AlarmRowHeight = v);
            if (AlarmScrollbarCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmScrollbar), AlarmScrollbarCheck.IsChecked == true, v => _currentControl.AlarmScrollbar = v);
            if (AlarmRefreshBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmRefreshMs), (int)AlarmRefreshBox.Value, v => _currentControl.AlarmRefreshMs = v);
            if (AlarmBlinkCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmBlink), AlarmBlinkCheck.IsChecked == true, v => _currentControl.AlarmBlink = v);
            if (AlarmBlinkMsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmBlinkMs), (int)AlarmBlinkMsBox.Value, v => _currentControl.AlarmBlinkMs = v);
            if (AlarmDateFormatBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmDateFormat), string.IsNullOrWhiteSpace(AlarmDateFormatBox.Text) ? null : AlarmDateFormatBox.Text.Trim(), v => _currentControl.AlarmDateFormat = v);
            if (AlarmTimeFormatBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmTimeFormat), string.IsNullOrWhiteSpace(AlarmTimeFormatBox.Text) ? null : AlarmTimeFormatBox.Text.Trim(), v => _currentControl.AlarmTimeFormat = v);'''
if 'AlarmOrderCombo.IsVisible' not in s:
    s = s.replace(a, b, 1)
    print('changes ok')
else:
    print('changes already')
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 挂 ValueChanged ----------
p = 'src/HmiEditor.App/Views/PropertyPanelView.axaml.cs'
s = open(p, encoding='utf-8', newline='').read()
a = '''        CurveLastMinBox.ValueChanged += OnNumberChanged;'''
b = a + '''
        // 批次 2c：报警列表
        AlarmRowHeightBox.ValueChanged += OnNumberChanged;
        AlarmRefreshBox.ValueChanged += OnNumberChanged;
        AlarmBlinkMsBox.ValueChanged += OnNumberChanged;'''
if 'AlarmRowHeightBox.ValueChanged' not in s:
    s = s.replace(a, b, 1)
    print('wiring ok')
else:
    print('wiring already')
open(p, 'w', encoding='utf-8', newline='').write(s)

# ---------- 字典 ----------
zh = '''  "prop.detail.alarm.order": "排序",
  "prop.detail.alarm.order.newest": "最新在前",
  "prop.detail.alarm.order.oldest": "最新在后",
  "prop.detail.alarm.ack": "确认方式",
  "prop.detail.alarm.ack.none": "不可确认",
  "prop.detail.alarm.ack.click": "单击确认",
  "prop.detail.alarm.ack.dblclick": "双击确认",
  "prop.detail.alarm.grid": "网格",
  "prop.detail.alarm.grid.all": "全部",
  "prop.detail.alarm.grid.rows": "仅行线",
  "prop.detail.alarm.grid.border": "仅边框",
  "prop.detail.alarm.grid.none": "无",
  "prop.detail.alarm.rowheight": "行高（0=自动）",
  "prop.detail.alarm.scrollbar": "显示滚动条",
  "prop.detail.alarm.refresh": "刷新周期（ms）",
  "prop.detail.alarm.blink": "未确认闪烁",
  "prop.detail.alarm.blinkms": "闪烁周期（ms）",
  "prop.detail.alarm.datefmt": "日期格式",
  "prop.detail.alarm.timefmt": "时间格式",
'''
en = '''  "prop.detail.alarm.order": "Order",
  "prop.detail.alarm.order.newest": "Newest first",
  "prop.detail.alarm.order.oldest": "Newest last",
  "prop.detail.alarm.ack": "Acknowledge on",
  "prop.detail.alarm.ack.none": "Not allowed",
  "prop.detail.alarm.ack.click": "Single click",
  "prop.detail.alarm.ack.dblclick": "Double click",
  "prop.detail.alarm.grid": "Grid",
  "prop.detail.alarm.grid.all": "All",
  "prop.detail.alarm.grid.rows": "Rows only",
  "prop.detail.alarm.grid.border": "Border only",
  "prop.detail.alarm.grid.none": "None",
  "prop.detail.alarm.rowheight": "Row height (0 = auto)",
  "prop.detail.alarm.scrollbar": "Show scrollbar",
  "prop.detail.alarm.refresh": "Refresh (ms)",
  "prop.detail.alarm.blink": "Blink unacknowledged",
  "prop.detail.alarm.blinkms": "Blink period (ms)",
  "prop.detail.alarm.datefmt": "Date format",
  "prop.detail.alarm.timefmt": "Time format",
'''
for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'prop.detail.alarm.order' in s:
        print('strings skip', p)
        continue
    anchor = '  "prop.curve.lastmin"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
    print('strings ok', p)

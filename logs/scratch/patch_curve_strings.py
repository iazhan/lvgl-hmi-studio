zh = '''  "prop.curve.title": "曲线设置",
  "prop.curve.axis": "轴线颜色",
  "prop.curve.bg": "绘图区底色",
  "prop.curve.border": "绘图区边框",
  "prop.curve.grid": "辅助线颜色",
  "prop.curve.tick": "刻度线颜色",
  "prop.curve.label": "刻度文字色",
  "prop.curve.showgrid": "画辅助线",
  "prop.curve.legend": "显示图例",
  "prop.curve.showaxis": "画轴线",
  "prop.curve.showlabels": "画刻度标签",
  "prop.curve.direction": "拖动方向",
  "prop.curve.dir.lr": "左→右",
  "prop.curve.dir.rl": "右→左",
  "prop.curve.xformat": "X 轴时间格式",
  "prop.curve.xformat.clock": "时钟",
  "prop.curve.xformat.rel": "相对秒",
  "prop.curve.xdiv": "X 主分隔数",
  "prop.curve.xminor": "X 次分隔数",
  "prop.curve.xstep": "X 标签间隔（0=自动）",
  "prop.curve.ydiv": "Y 主分隔数",
  "prop.curve.yminor": "Y 次分隔数",
  "prop.curve.ydecimals": "左轴小数位",
  "prop.curve.ydigits": "左轴整数位",
  "prop.curve.y2min": "右轴下限",
  "prop.curve.y2max": "右轴上限",
  "prop.curve.y2decimals": "右轴小数位",
  "prop.curve.y2digits": "右轴整数位",
  "prop.curve.lastmin": "区间（分钟，0=用窗口）",
'''

en = '''  "prop.curve.title": "Curve settings",
  "prop.curve.axis": "Axis color",
  "prop.curve.bg": "Plot background",
  "prop.curve.border": "Plot border",
  "prop.curve.grid": "Grid color",
  "prop.curve.tick": "Tick color",
  "prop.curve.label": "Label color",
  "prop.curve.showgrid": "Draw grid",
  "prop.curve.legend": "Show legend",
  "prop.curve.showaxis": "Draw axis",
  "prop.curve.showlabels": "Draw labels",
  "prop.curve.direction": "Pan direction",
  "prop.curve.dir.lr": "Left to right",
  "prop.curve.dir.rl": "Right to left",
  "prop.curve.xformat": "X time format",
  "prop.curve.xformat.clock": "Clock",
  "prop.curve.xformat.rel": "Relative seconds",
  "prop.curve.xdiv": "X major divisions",
  "prop.curve.xminor": "X minor divisions",
  "prop.curve.xstep": "X label step (0 = auto)",
  "prop.curve.ydiv": "Y major divisions",
  "prop.curve.yminor": "Y minor divisions",
  "prop.curve.ydecimals": "Left axis decimals",
  "prop.curve.ydigits": "Left axis integer digits",
  "prop.curve.y2min": "Right axis min",
  "prop.curve.y2max": "Right axis max",
  "prop.curve.y2decimals": "Right axis decimals",
  "prop.curve.y2digits": "Right axis integer digits",
  "prop.curve.lastmin": "Range (minutes, 0 = window)",
'''

for p, block in [('src/HmiEditor.Core/Resources/Strings.zh-CN.json', zh),
                 ('src/HmiEditor.Core/Resources/Strings.en-US.json', en)]:
    s = open(p, encoding='utf-8').read()
    if 'prop.curve.title' in s:
        print('skip', p)
        continue
    anchor = '  "prop.detail.tab.pos.right"'
    i = s.index(anchor)
    j = s.index('\n', i) + 1
    s = s[:j] + block + s[j:]
    open(p, 'w', encoding='utf-8').write(s)
    print('ok', p)

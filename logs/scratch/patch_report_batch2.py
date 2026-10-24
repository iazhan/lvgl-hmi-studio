p = 'MAPPING-REPORT.md'
s = open(p, encoding='utf-8').read()

anchor = '''  默认值（normal / 0 / ok / 40 / 10 / 200 / 4）一律不写，产物保持最小。'''
add = anchor + '''
- **批次 2（v2.4.0 ~ v2.7.0）补齐的曲线与报警**：
  - `history_curve` 补 24 项（轴线/绘图区底色/边框/辅助线/刻度线/文字色、拖动方向、网格与图例、
    显示轴线/标签、X/Y 主次分隔、X 标签间隔、X 时间格式、左右轴小数位与整数位、右轴上下限、
    区间分钟数）；
  - **新增 `realtime_curve`**（采样曲线）：与历史曲线共用外观面板，写 `sample_ms`/`history_s`/`span_s`，
    不写 `group`/`range`/`refresh_ms`；外观与刻度由 `MapCurveCommon` 两条曲线共用一份；
  - `alarm_list` 补 10 项（排序/确认方式/网格/行高/滚动条/刷新周期/闪烁与周期/日期与时间格式）；
  - **新增 `alarm_bar`**（单行滚动报警条）：12 项 props，`order` 只有 newest_first/newest_last。

  三个枚举（`alarm_list.order/ack_on/grid`、`alarm_bar.order`）**先过白名单再写**——运行时对它们是
  硬校验，非法值直接拒绝整页。'''
if '批次 2（v2.4.0 ~ v2.7.0）' not in s:
    s = s.replace(anchor, add, 1)

old = s[s.index('> 批次 1b 之后仍缺的属性'):s.index('## 4 删掉的旧路径')]
new = '''> 批次 2 之后**编辑器侧属性缺口已清零**。剩下的都是运行时侧或契约本身的问题：
> `gauge.label_pos`（白名单里有、实现没读）、`svg.kind`（运行时没有 SVG）、
> `label.font_style`（要第二套字体）、`input.commit`（失焦写回属行为改动）、
> `pipe` 的 5 项（契约把 pipe 同时列在 widgets 与 figures，运行时按 figure 建）。

'''
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
print('report ok')

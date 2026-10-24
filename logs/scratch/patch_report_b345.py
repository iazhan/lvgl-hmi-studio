p = 'MAPPING-REPORT.md'
s = open(p, encoding='utf-8').read()

add = '''
## 8 批次 3/4/5 之后的覆盖面（v3.1.0）

| 维度 | 契约 | 编辑器 | 说明 |
|---|---|---|---|
| 控件 widgets | 33 | **33** | 批次 3（qrcode/message/enum_text/roller/state_image/frame_anim）+ 批次 5（chart/xy_curve/history_table/recipe_table/ascii_display）补齐 |
| 图元 figures | 40 | **40** | 批次 4 收成「图元」控件 + 种类下拉（`ShapeKind`，白名单集中在 `ShapeKinds`） |

**三处「契约比实现宽」，编辑器按实现收紧**（否则导出后被运行时整页拒绝）：

1. `history_table.columns[].key` —— 运行时只认 `time` 或**数值 tag id**；契约写的
   level/name/value/text/state/ack 与 `key="tag"` 写法都会让整页失败；
2. `ascii_display.encoding` —— 运行时只认 `gbk`/`utf8`，契约写的是 `gbk`/`ascii`；
3. `gauge.ranges` / `enum_text.map` —— 契约放在 props，运行时只读 **bind[] 里**的同名项。

另外补了一条契约缺口：`alarm_bar.color` 运行时读、契约没列（已加进 spec）。

**链路缺陷（已修）**：图元的 `kind` 之前只当局部变量用于白名单过滤、**从没写进 JSON**，
导出的图元全靠运行时默认值 —— 等于"选什么形状都画成同一个"。
'''

if '## 8 批次 3/4/5 之后的覆盖面' not in s:
    s = s.rstrip('\n') + '\n' + add
open(p, 'w', encoding='utf-8').write(s)
print('report ok')

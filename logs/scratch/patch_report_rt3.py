p = 'MAPPING-REPORT.md'
s = open(p, encoding='utf-8').read()
n = 0

# 1) gauge 段：label_pos 已补
old = '''  仍未实现：`ranges`（运行时现有的 ranges 走 `bind[].kind=ranges`，与契约把它放在 props
  不一致，需要单独设计）、`label_pos`（见下条）。'''
new = '''  `label_pos` 也已补（0.49.0：inside 贴弧内侧 / outside 放到控件外侧下方）。
  仍未实现：`ranges`（运行时现有的 ranges 走 `bind[].kind=ranges`，与契约把它放在 props
  不一致，需要单独设计）。'''
if old in s:
    s = s.replace(old, new, 1)
    n += 1

# 2) 三条"仍缺"改成已补
old2 = '''- 仍「契约有、实现没读」的只剩：`input.commit`（涉及键盘提交时机，属行为改动）、
  `label.font_style`（粗/斜体需要第二套字体文件，运行时只有一套）、
  `figure container.layout`。'''
new2 = '''- **0.49.0 又补了三项**（编辑器侧早就导出、运行时一直忽略的）：
  `label.font_style`（FreeType 合成粗/斜体，**不需要第二套字体文件**）、
  `input.commit`（keypad 加 `on_close_*`：`blur` 时"这次输入结束"就写回，取消也写）、
  `gauge.label_pos`。
- 仍「契约有、实现没读」的只剩 `figure container.layout`。'''
if old2 in s:
    s = s.replace(old2, new2, 1)
    n += 1

# 3) pipe 那行改成"已对齐"（报告里是单行）
old3 = '> `pipe` 的 5 项（契约把 pipe 同时列在 widgets 与 figures，运行时按 figure 建）。'
new3 = '''> `pipe` 的 5 项**已对齐（v2.8.0）**：契约里 pipe 是**控件**（`widgets.pipe`，6 个 props），
> 运行时也有 `W_PIPE` 分派（`build_pipe` 带流动动画）；错的是编辑器一直导成 shape 的
> `pipe_h/pipe_v`（图元版只读 `wall`）。现在 `TargetType → "pipe"` 并写全
> `direction`/`wall`/`color`/`channel_color`/`flow_ms`，同时在 `manifest.features` 声明 `pipe`。
>
> ⚠ 顺手记一个坑：**删掉 `widgets.pipe` 会让 golden 6 个项目失败** —— g02 与 allinone
> 里真的有 `type: "pipe"` 的控件，说明契约条目是对的，错的是编辑器映射。'''
if old3 in s:
    s = s.replace(old3, new3, 1)
    n += 1

open(p, 'w', encoding='utf-8').write(s)
print('replaced blocks:', n)

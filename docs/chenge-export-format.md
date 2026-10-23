# 导出产物格式（chenge-runtime JSON 工程，format 0.6）

本分支的编辑器只有一条导出路径：把工程写成 `chenge-runtime` 能直接加载的 JSON 目录。
逐字段的权威定义在 `f1c100s-project/docs/chenge-rt/37-组态工程文件规范.md` 与
`f1c100s-project/spec/widget-schema.json`；本文只说明**编辑器这一侧**写出什么、
哪些东西会阻断导出。

## 1. 目录结构

| 文件 | 何时写 | 说明 |
| --- | --- | --- |
| `manifest.json` | 总是 | `format_version` / `hw_profile` / `start_page` / `screen` / `features[]` / `fonts` / `theme` |
| `pages/<页面id>.json` | 每屏一个 | `widgets[]`：`id` / `type` / `rect` / `z` / `props` / `bind` / `events` |
| `tags.json` | 总是 | 变量表：`id` 从 0 顺序分配、`name` 工程内唯一、`type` 取 `i16/u16/i32/u32/f32/i64/double/string/bool` |
| `devices.json` | 总是（可为空数组） | 设备与协议参数、通道参数 |
| `ports.json` | 总是（可为空数组） | 逻辑串口：`port`（`com1`/`com2`…）→ 设备节点/波特率 |
| `alarms.json` | 有报警组时 | 报警组与条目，含 `text_id` / `text_clear_id` |
| `savecfg.json` | 有存盘组时 | 存盘组：`max_records` / `full_policy` / `csv_delimiter` / `csv_time_format` / `record_timestamp` / `storage_path` |
| `users.json` | 有运行时用户时 | 用户 + `groups[].perms[]`（10 个命名权限位，词表 `spec/perms.json`） |
| `strings.json` | 有多语言文本时 | 文本 id → 各语言取值；`{tag:id\|fmt\|@空值键}` 的 `@` 键也在里面 |
| `assets/*.bin` | `BundleAssets` 打开且有图片资产 | RGB565 原始位图 |

## 2. 映射约定

- **坐标**：控件 `X/Y/Width/Height` 原样写进 `rect`（整数像素，屏幕坐标）。
- **颜色**：`#RRGGBB` / `#AARRGGBB` 统一转 `0xRRGGBB`；主题在导出时**烘进**控件的颜色与字号，
  不生成 `theme.json`（运行时不解析主题）。
- **绑定**：`bind` 的键是**契约属性名**（`text` / `value` / `checked` / `selected` / `level` …），
  值里 `tag` 是 `tags.json` 的数值 id（不是变量名）。
- **动作**：一次事件只允许一个动作；`write_tag` 写布尔变量时值必须是 `true`/`false`，
  发 `1`/`0` 会被运行时按「数值写布尔」拒绝。
- **单位符号**：只用 `×` / `✓`（`✗` `✔` `☑` 在板端字体里没有字形）。
- **文本占位符**：`{tag:id}` / `{tag:id|fmt}` / `{tag:id|fmt|空值}`，空值以 `@` 开头时取
  `strings.json` 的键。

## 3. 会阻断导出的问题（`ChengeIssues`）

- 控件属性不在 `spec/widget-schema.json` 里（例如把 LVGL 的旋转/镜像拿来当控件属性）；
- 动作缺目标变量、目标变量不存在、写动作没有值、同一事件挂多个动作；
- 报警列表引用的报警组不存在、历史曲线引用的存盘组不存在；
- 变量名为空或重复、`tags.json` 里出现两份同名；
- `com2` 是 RS232 却挂了多个设备（37 §4）；
- 文本 id 不符合 `[a-z][a-z0-9_.-]{0,63}`。

Warning 不阻断，但会在导出结果里列出来（例如硬件目标不在已知清单里）。

## 4. 验证方式

```bash
# 宿主运行时（先在该目录 make HOST=1）
HMI_PROJECT_DIR=<导出目录> HMI_DATA_DIR=/tmp/x SDL_VIDEODRIVER=dummy ./chenge-runtime
```

日志出现 `page: <id> 打开` 即建页成功；出现「建页失败」说明导出物不合法。
回归用 `chenge-runtime/tools/golden.sh`（54 例）。

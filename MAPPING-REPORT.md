# MAPPING-REPORT —— 编辑器单轨化（只导 chenge-runtime 工程）

分支 `chenge-hmi-studio`，本轮把导出收敛成**一条**路径：工程 → chenge-runtime JSON 目录（格式 0.6）。
C 源码导出、`.so` 编译、模拟器工程、下载到 HMI、设备管理**全部删除**，不保留兼容开关。

运行时侧只读，**未改动**任何 `chenge-hmi/chenge-runtime` 文件。

---

## 1 覆盖到的控件（编辑器 → 契约 type）

| 编辑器控件 | 契约 type | 备注 |
| --- | --- | --- |
| Label | `label` | 文本/字号/颜色/对齐 |
| Value | `value` | 小数位/单位（`ValueSuffix` 可作单位回落） |
| Button | `button` | `events.click` |
| Switch | `switch` | `bind.checked`，写回不需要额外动作 |
| CheckBox | `checkbox` | |
| Led | `lamp` | 指示灯色 / 绑定 `level` |
| Bar / Slider | `bar` / `slider` | 值绑定 + 范围 |
| Gauge / Knob | `gauge` / `knob` | 刻度/指针参数 |
| Dropdown | `combobox` | 选项 + `bind.selected` |
| TextArea | `input` | 占位符 / 最大长度 |
| Image | `image` | 资产转 RGB565 `.bin` |
| Table | `table` | 行列 / 表头 / 单元格（含变量单元格） |
| TabView | `tabview` | 页签名 |
| Spinner | `spinner` | |
| Keyboard | `keyboard` | 只导配置，**不导出键盘控件本身**（运行时按输入焦点弹） |
| Rectangle / Ellipse / Polyline / Arc / Line / Tank / Pipe | `shape` | 按 kind 映射 |
| AlarmList（本轮新增） | `alarm_list` | 报警组过滤 / 模式 active·history / 行数 / 列集合 |
| HistoryCurve（本轮新增） | `history_curve` | 采集组名 / 窗口分钟 / 通道变量 |
| Valve、Svg、嵌套 Screen | — | 不导出（见 §3） |

本轮在编辑器侧补齐了三类控件的**编辑能力**：`Value` / `AlarmList` / `HistoryCurve` 进了
`HmiControlType`、工具箱、画布占位渲染、属性面板（单位 / 报警组 / 模式 / 行数 / 采集组 /
窗口 / 通道），并同步了中英文字典。

## 2 覆盖到的属性与子系统

- **几何**：`X/Y/W/H` → `rect`；`z` 保留；旋转/镜像**不导出**（见 §3）。
- **颜色/字号**：主题在导出期**烘进**控件颜色与字号，不生成 `theme.json`。
- **绑定**：`VariableBindings`（属性名 → `bind.<prop>`，值里是 tag id），
  方向只认 read / readwrite；契约 `bindable` 列表外的属性跳过并给 warning。
- **动作**：`write_tag` / `toggle_tag` / `goto_page` / `set_language` / `export_datalog` /
  `run_script` 等；一次事件只允许一个动作（多个即阻断）。
  布尔变量写值发 `true`/`false`（发 `1` 会被运行时按「数值写布尔」拒绝——本轮实测踩到并修好）。
- **变量表** `tags.json`：id 从 0 顺序分配、工程内名字唯一、类型映射
  `i16/u16/i32/u32/f32/i64/double/string/bool`、source（device/area/address/scale/byteorder）、
  死区、初值、单位、权限。
- **设备与串口** `devices.json` / `ports.json`：协议参数、逻辑串口映射、
  `com2` 为 RS232 时只允许挂 1 个设备（37 §4）。
- **报警** `alarms.json`：组 + 条目，条件（限值/死区）、等级、需确认、
  `text_id` / `text_clear_id`。
- **存盘** `savecfg.json`：`max_records` / `full_policy(overwrite|stop)` / `csv_delimiter` /
  `csv_time_format` / `record_timestamp` / `storage_path`。
- **用户与权限** `users.json`：`groups[].perms[]` 为 10 个命名权限位，词表 `spec/perms.json`。
- **文本** `strings.json`：多语言 + 占位符三种写法 `{tag:id}` / `{tag:id|fmt}` / `{tag:id|fmt|@空值键}`。
- **特性开关**：`manifest.features[]` 按工程实际用到的子系统声明（curves/alarm/datalog/…）。
- **批次 1a（v2.2.0）补齐的细节属性**（面板在「控件细节」区）：
  - `gauge`：`arc_width` / `tick_len` / `needle_len` / `needle_width` / `center_radius` / `ranges` / `show_value`；
  - `lamp`：`size` / `ranges`；
  - `button`：`confirm{text,yes,no,wait_ms}` / `hold_ms` / `long_press_ms` / `off_style`。

  色段写法 `50:#38C172,80:#F5A524,:#E5484D`（`值:颜色` 逗号分隔，末段省略阈值=兜底）。
  越界值**钳制并告警**：运行时对 `hold_ms`（0..5000）与 `long_press_ms`（300..5000）是硬校验，
  越界会**拒绝整页**，所以不能让一个数字把整页带死。
  按钮的按下态（`pressed`）不另开字段，仍由 `VisualStates[Pressed]` 表达（避免两套互相覆盖）。
- **批次 1b（v2.3.0）补齐的细节属性**：
  - `switch`：`color_on` / `color_off` / `knob_color`；
  - `bar`：`direction`（横/纵）、`ranges`、`show_percent`；
  - `spinner`：`arc_len` / `arc_width`（`speed_ms` 本来就有）；
  - `label`：`font_style`（运行时暂不支持粗/斜体）、`letter_spacing` / `line_spacing`；
  - `input`：`commit`（运行时暂不支持失焦写回）；
  - `knob`：`arc_width`；
  - `tabview`：`tab_h` / `tab_pos`。

  默认值（normal / 0 / ok / 40 / 10 / 200 / 4）一律不写，产物保持最小。
- **批次 2（v2.4.0 ~ v2.7.0）补齐的曲线与报警**：
  - `history_curve` 补 24 项（轴线/绘图区底色/边框/辅助线/刻度线/文字色、拖动方向、网格与图例、
    显示轴线/标签、X/Y 主次分隔、X 标签间隔、X 时间格式、左右轴小数位与整数位、右轴上下限、
    区间分钟数）；
  - **新增 `realtime_curve`**（采样曲线）：与历史曲线共用外观面板，写 `sample_ms`/`history_s`/`span_s`，
    不写 `group`/`range`/`refresh_ms`；外观与刻度由 `MapCurveCommon` 两条曲线共用一份；
  - `alarm_list` 补 10 项（排序/确认方式/网格/行高/滚动条/刷新周期/闪烁与周期/日期与时间格式）；
  - **新增 `alarm_bar`**（单行滚动报警条）：12 项 props，`order` 只有 newest_first/newest_last。

  三个枚举（`alarm_list.order/ack_on/grid`、`alarm_bar.order`）**先过白名单再写**——运行时对它们是
  硬校验，非法值直接拒绝整页。

## 3 暂不支持（连原因一起记）

| 项 | 为什么 |
| --- | --- |
| Svg 控件 | 运行时没有 SVG 解析器；把多图元展平成一次性 polygon 会丢语义，宁可不导并给 warning |
| Valve 控件 | 契约里没有阀门图元（可用 shape + 两个状态色代替） |
| 嵌套 Screen 控件 | 页面切换用 `goto_page` 动作；嵌套屏幕在契约里没有表达 |
| 控件旋转 / 镜像 | 运行时没有 `rotation` / `mirror_x` / `mirror_y` 的实现；面板仍可设，导出会给 warning |
| 视觉状态（visual states） | 契约里状态切换靠动作 + 属性写入，没有「状态机」这一层；导出给 warning |
| 键盘控件本体 | 运行时按输入焦点自己弹键盘，工程里只需要配置 |
| CVS/图表之外的高级图元参数 | 以 `spec/widget-schema.json` 的 `props` 白名单为准，白名单外的属性会被跳过并告警 |

这些都在导出结果里以 **Warning** 出现（不是静默丢弃）；Error 才会阻断导出。

> 批次 2 之后**编辑器侧属性缺口已清零**。剩下的都是运行时侧或契约本身的问题：
> `gauge.label_pos`（白名单里有、实现没读）、`svg.kind`（运行时没有 SVG）、
> `label.font_style`（要第二套字体）、`input.commit`（失焦写回属行为改动）、
> `pipe` 的 5 项**已对齐（v2.8.0）**：契约里 pipe 是**控件**（`widgets.pipe`，6 个 props），
> 运行时也有 `W_PIPE` 分派（`build_pipe` 带流动动画）；错的是编辑器一直导成 shape 的
> `pipe_h/pipe_v`（图元版只读 `wall`）。现在 `TargetType → "pipe"` 并写全
> `direction`/`wall`/`color`/`channel_color`/`flow_ms`，同时在 `manifest.features` 声明 `pipe`。
>
> ⚠ 顺手记一个坑：**删掉 `widgets.pipe` 会让 golden 6 个项目失败** —— g02 与 allinone
> 里真的有 `type: "pipe"` 的控件，说明契约条目是对的，错的是编辑器映射。

## 4 删掉的旧路径

| 删除对象 | 内容 |
| --- | --- |
| `src/HmiEditor.CodeGen/` | 整个 C 代码生成程序集（CodeGenerator 各分部、发射器、构建系统生成） |
| `src/HmiEditor.Device/` | 设备通道 / 检测 / OTA / FEL 烧录 / T113 `.so` 交叉编译（`T113SoBuilder`） |
| `src/HmiEditor.Core/CodeGen/` | 权限/资源/命名等 C 侧符号契约（`CIdentifier`、`SecurityConfigCodeGen`、`RuntimeConfigCodeGen` …） |
| `src/HmiEditor.Core/Mapping/` | LVGL 控件映射表（只服务 C 发射） |
| `Core/Services` 里 5 个 | `SimulatorProjectExporter*`（CMake 模拟器工程）、`GeneratedCodeValidator`、`GeneratedCodeIssueCodes`、`CommConfigCodeGen` |
| 编辑器 UI | `BuildRunDialog`、`DownloadToHmiView`、`DeviceManagerView`、`ValidationResultView` 及其菜单/工具栏/快捷键；`SimulatorBuildService`、`BuildPipelineService` |
| 工具栏图标 | `IconCompile`、`IconDownloadHmi`（资源键随按钮一起删） |
| `tools/` | `SimExport`、`DeviceExport`、`ReproHarness`、`ReleasePackager`、`BudgetCheck`、`T113KitBuilder` |
| 测试 | 旧路径的 80+ 个用例文件；`HmiEditor.Device.Tests` 整个项目 |
| 本地化 | 220 条只被旧界面使用的字典键 + 185 个对应 `Lang` 常量；`gen.issue.*` / `gen.loc.*` 两族键 |

**预览**改成「导出到临时目录 + 宿主 `chenge-runtime` + `HMI_SHOT` 截图」
（`Services/ChengeExportService.cs`，菜单「导出并预览」）；`.so` 模拟器那条整条删除。

模型层保留：控件模型、图库/图元、属性面板、schema 驱动的编辑器全部不动。

## 5 验证

- `dotnet build HmiEditor.slnx`：0 错误。
- `dotnet test HmiEditor.slnx`：**全绿**（Core 1476、App.UiTests 783、AI 315、Simulator 44、
  Hardware 32、Communication 84、Render 4、Export.Chenge 6）。
- 新增 `tests/HmiEditor.Export.Chenge.Tests`：9 类控件的导出映射、props 必须落在
  `widget-schema.json` 白名单内、动作缺 tag / 一个事件两个动作都要阻断；
  以及**宿主运行时段**（设 `CHENGE_RUNTIME_DIR` 时生效）：导出 → 跑
  `chenge-runtime`（SDL dummy）→ 断言 `page: home 打开` → 注入点击 → 断言 `启动=1` → 截图存在。
- 运行时侧 `tools/golden.sh`：**54 例全过**。
- 运行时侧文件未改（`git status` 干净）。

## 6 发现的契约不符（只记录，未改运行时）

1. **布尔写值口径**：`write_tag` 给 bool tag 发 `1` 会被 `tagdb` 拒绝
   （`tagdb: tagN 是布尔，数值写被拒绝`）。契约文档没写这一条，编辑器侧现在按 tag 类型转 bool。
   建议 37 规范补一句「bool 只接受 true/false」。
2. **每页控件上限**是镜像级编译参数（`core/limits.h`，默认 150），不在 `manifest.json` 里声明；
   编辑器无法在导出期预检，超限只能等运行时报「建页失败」。
3. **主题**：契约里 `manifest.theme` 只是一个编号，运行时按内置主题取色；编辑器侧只能烘颜色，
   两者不一致时没有告警。建议 37 明确「theme 只是默认值，控件显式颜色优先」。
4. **文本占位符空值档**：`{tag:id|fmt|@key}` 的 `@` 约定只在运行时解析器里实现，
   `spec/widget-schema.json` 没有对应字段说明；编辑器按文档实现，契约文件里查不到依据。
5. **报警 text_id / text_clear_id**：运行时期望 `strings.json` 的键，但没有校验键是否存在；
   编辑器目前只在缺失时给 Warning，不阻断。
6. **`features[]`**：`spec/features.json` 列了模块的 `build` 开关，编辑器无法知道目标镜像实际编了哪些，
   只能按工程用到的能力声明；镜像关掉某能力时装载会被运行时拒绝，错误信息在编辑器侧不可见。

- **gauge 的细节属性已在运行时补齐（0.48.0）**：`arc_width`（弧/环）、`tick_len`、
  `needle_len`（指针长度百分比）、`needle_width`、`center_radius` 都读 props 了，
  缺省仍走历史值（弧 8 / 环 6 / 刻度 6、7 / 指针 4px / 轴心 5），老工程与 golden 不变。
  实测：只改这 5 项 → gauge 区域像素差异 2.2%（补齐前 0.00%）。
  `label_pos` 也已补（0.49.0：inside 贴弧内侧 / outside 放到控件外侧下方）。
  仍未实现：`ranges`（运行时现有的 ranges 走 `bind[].kind=ranges`，与契约把它放在 props
  不一致，需要单独设计）。
- **同批补齐（0.48.0）**：`label.letter_spacing` / `line_spacing`、`switch.knob_color`、
  `value.prefix` / `suffix`（只作用于 value，不作用于 input）。实测整屏差异 0.83%。
- **0.49.0 又补了三项**（编辑器侧早就导出、运行时一直忽略的）：
  `label.font_style`（FreeType 合成粗/斜体，**不需要第二套字体文件**）、
  `input.commit`（keypad 加 `on_close_*`：`blur` 时"这次输入结束"就写回，取消也写）、
  `gauge.label_pos`。
- 「契约有、实现没读」的属性：**已清零**。最后一条 `figure container.layout` 是误记 ——
  契约里它的枚举只有 `free` 一个值（v1 就只定义了这一种布局），运行时不需要读它；
  容器本身是**实现了**的（`ui_page.c` 的 `flatten_widgets` 递归展开 `props.children`，
  建对象时按 `s_wparents` 挂到父对象下，子控件坐标相对父）。
  ⇒ **真正的缺口在编辑器**：导出器从不写 `props.children`（`grep '"children"'` 在 mapper 里
  零命中），所以在编辑器里画的容器只是个空框，放不进子控件。

- `gauge.props.label_pos` 在契约白名单里，但运行时（0.47.0）**没有任何解析代码**：
  `grep -rn label_pos chenge-runtime/src` 只命中 `widgets_schema.h` 的白名单。
  ⇒ 编辑器**不导出**这个属性，避免写一个没人读的字段（面板也不提供）。

## 7 已知遗留

- 属性面板 `PropertyPanelWiringTests` 的枚举夹具还没有 AlarmList / HistoryCurve 两类，
  两个数字框（`CgAlarmRowsBox` / `CgSpanMinBox`）以「豁免 + 理由」登记，**待补夹具**。
- `ExportConfig` 里仍留有 C 生成时代的字段（`CodeStyle` / `ResourceNaming` / `MergeResources` 等），
  工程设置里标为「不生效」。彻底清理要动 `.hmi` 工程文件格式，本轮未做。
- `SettingsService` 的 `KinSeal.ToolchainGcc` / `KinSeal.RuntimeDir` / `T113*` 已经**没有消费者**
  （`.so` 编译那条链删了），设置界面仍显示，属于待清理项。

## 8 批次 3/4/5 之后的覆盖面（v2.11.0）

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

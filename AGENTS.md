# AGENTS.md —— 本仓库的 AI 协作约定

> 只写**本仓库特有**的纪律；通用纪律见用户级 `~/.codebuddy/rules/`，
> 运行时 / 板端 / 导出契约的事实见主仓库 `f1c100s-project/docs/`（地图：`docs/README.md`）。
> 本仓库不放产品文档：结论写进主仓库 `docs/`，这里只留指针。

## 1 改完跑什么

| 改动范围 | 命令 |
|---|---|
| 任何 C# / XAML | `dotnet test tests/HmiEditor.App.UiTests` + `tests/HmiEditor.Core.Tests` |
| 导出器 | `CHENGE_RUNTIME_DIR=<主仓库>/chenge-hmi/chenge-runtime dotnet test tests/HmiEditor.Export.Chenge.Tests`（没有宿主二进制时端到端那几条会静默跳过，别把它当通过） |
| 运行时（`chenge-runtime`） | 见 §4：**两个产物都要编** |

## 2 属性面板（`PropertyPanelView*`）

**行数闸门**：`PropertyPanelView.*.cs` 单个分部 ≤ 500 行、`MainWindow.axaml.cs` 有精确基线，
判据在 `tests/HmiEditor.Core.Tests/SingleFileSizeGuardTests.cs`。加行加不下就**按控件族拆新分部文件**，
文件名写"服务哪一类控件"（`Parameters.Media.cs` / `Changes.Content.cs`），**不要**用排期批次命名。

**改 XAML 前必读三条坑**（都不报错，只表现为"面板看着不对"）：

| 坑 | 后果 | 判据 |
|---|---|---|
| `Grid.Row` 超出行定义数 | 超出的控件**全部叠在最后一行**（"参数被压成一行"） | `PropertyPanelLayoutTests.EveryGrid_DeclaresEnoughRowsForTheHighestRowItUses` |
| `RowSpacing` 对隐藏行照样生效 | 88 行 × 4px ≈ 350px 空白；`Expander Grid` 全局样式给 4，局部要**显式** `RowSpacing="0"` | `HiddenRows_DoNotLeaveVerticalGap`、`AppearanceGroup_HasNoLeadingGap` |
| 切显隐只切了行内控件 | 空行仍占 `Spacing`，顶部照样空白 —— **要切整行 `Grid`** | 同上 |

**新增具名控件必须登记**，否则三条普查测试会红：

- `tests/HmiEditor.App.UiTests/PropertyPanelTextBoxWiringTests.cs` 的 `WithoutWiring`（XAML 属性式接线）
- `tests/HmiEditor.App.UiTests/AppXamlAttributeWiringFaceTests.cs` 的登记表（`TextChanged="…"` 要逐条登记）
- `tests/HmiEditor.App.UiTests/PropertyPanelWiringTests.cs` 的 `Exemptions`（整块显隐、夹具里没有该类型时，**必须写理由**）

**显隐的判据是"运行时真的读"**：面板上能改的键，必须确认运行时那侧有 `hmi_json_get` 消费点。
名单的单一来源是 `PropertyPanelView.AppearanceRows.cs`，别在别处再写一份。

## 3 本地化

- 文案进 `src/HmiEditor.Core/Resources/Strings.zh-CN.json` **和** `en-US.json`（两边键必须成对）
- XAML 用 `{res:Loc key}`；C# 用 `Lang.XXX` 常量。键的可达性 / 常量引用都有守卫测试
- 标签列宽 70px ≈ 5 个汉字：长说明放 `PlaceholderText` 或 `ToolTip.Tip`，不要撑爆标签

## 4 设计画布用的是 `chenge-preview-host`，不是 `chenge-runtime`

编辑器画布（隔离预览）只认主仓库 `chenge-hmi/chenge-runtime/chenge-preview-host`。
**改运行时代码后必须两个产物都编**：`make HOST=1`（截图 / golden / 导出测试用）与
`make PREVIEW=1 HOST=1`（画布用）。只编前者时画布跑旧二进制，现象是"改了没反应"而所有测试全绿。
排查：`readlink /proc/<pid>/exe`，带 `(deleted)` = 编了没重启编辑器。详见主仓库 `docs/chenge-rt/51`。

## 5 版本与提交

- 版本号唯一来源 `src/HmiEditor.Core/VersionInfo.cs`：**major 固定 2**，新功能 `2.x.0`、修缺陷 `2.x.y`；
  一个功能块只升一次，在交付那次升
- 提交标题：中文、不带区域前缀、末尾括注版本号（看 `git log` 的近几条）；
  正文写"改了什么 + 为什么"，不写改动史流水账

# 晨歌 HMI 编辑器（chenge-hmi）

工业现场的 HMI 组态编辑器：在 PC 上画画面、配变量与动作，导出**一份 chenge 工程（JSON，格式 0.6）**，
交给板上的设备侧运行时渲染。预览用的是与板端同源的渲染代码，不是另写一套模拟器。

| 项目 | 值 |
|---|---|
| 编辑器 | C# / .NET 10 + Avalonia 12.1（Linux 为主力开发平台） |
| 目标板 | 全志 F1C100S（kinseal）：800×480 RGB666 电阻触摸、32 MiB DDR、SD 卡启动 |
| 设备侧运行时 | `chenge-runtime`（LVGL 9.5 / ABI 3），在姊妹仓库 `f1c100s-project` |
| 导出产物 | chenge 工程目录（格式 0.6），可打成 `.cgpkg` 由 U 盘导入 |
| 编辑器版本 | `2.27.0`，唯一来源 `src/HmiEditor.Core/VersionInfo.cs` |

## 仓库结构

| 路径 | 内容 |
|---|---|
| `src/` | **11** 个 C# 项目：`HmiEditor.App`（Avalonia 界面）、`HmiEditor.Core`（模型 / 服务 / 导出契约）、`HmiEditor.Export.Chenge`（工程写出）、`HmiEditor.Simulator`、`HmiEditor.AI`，以及硬件与通讯各三件 `Contracts` / `Builtin` / `Loader` |
| `tests/` | **8** 个测试项目（Core、headless UI、导出、渲染、模拟器、AI、硬件、通讯）+ `HmiEditor.TestSupport`（含契约语料与字体夹具） |
| `tools/` | 出包（`package.sh` 入口 + `publish-linux.sh` / `publish-win.sh` / `sync-runtime.sh` / `installer/`）、`MockHmiGen`（Golden 夹具工程）、SDK 打包、若干一次性探针 |
| `scripts/` | 门禁与检查脚本，入口 `00_gate.sh` |
| `runtime/` | 出包用的运行时兜底副本（`chenge-preview-host` 等），见 `runtime/README.md` |
| `docs/` | 三份在用文档 + 地图（`docs/README.md`） |
| `design-previews/` | 设计 token 的 CSS 与页面原型，`DesignTokenCatalogTests` 读它 |
| `code-assistant-devkit/` | AI 助手开发套件（独立子项目，不在 `HmiEditor.slnx` 里） |

设备侧（C 运行时、板级树、固件构建）不在本仓库：那是姊妹仓库 `f1c100s-project` 的
`chenge-hmi/chenge-runtime` 与 `f1c100s-os/chenge-bsp`。

## 环境要求

| 用途 | 需要 |
|---|---|
| 构建 / 测试 | .NET SDK **10.0.100+**（`global.json` 固定，`rollForward: latestFeature`）、bash |
| 跑界面 | Linux：X11 / Wayland 桌面；Windows：直接运行 |
| 打 Windows 包 | `mingw-w64`（`x86_64-w64-mingw32-gcc-posix`）、NSIS（`makensis`）；可以在 Linux 上交叉出包 |
| 打 Linux 包 | `dpkg-deb`、`zip` |

## 快速开始

```bash
dotnet build HmiEditor.slnx -c Release
dotnet test  HmiEditor.slnx -c Release --no-build
dotnet run --project src/HmiEditor.App/HmiEditor.App.csproj -c Release
```

编辑器找运行时（设计画布 / 实时预览 / 截图）的顺序是**显式**的：
设置项 `chenge.runtimeDir` → 环境变量 `CHENGE_RUNTIME_DIR` → `<安装目录>/chenge-runtime`。
开发机想指到别处就设环境变量：

```bash
CHENGE_RUNTIME_DIR=$PWD/runtime/linux-x64 dotnet run --project src/HmiEditor.App/HmiEditor.App.csproj
```

## 出包

```bash
bash tools/package.sh                    # 交互：问平台
bash tools/package.sh --linux            # tar.gz + .deb
bash tools/package.sh --windows          # zip + NSIS Setup.exe（Linux 上交叉出）
bash tools/package.sh --all --build-runtime <chenge-runtime 目录>   # 现编运行时再打包
```

产物落在 `dist/`。不传 `--runtime-*` 时用仓库内 `runtime/` 的兜底副本；安装包里自带一份运行时，
装完不需要再填路径。运行时目录是显式参数 —— 脚本不猜主仓库在哪。

## 门禁

```bash
bash scripts/00_gate.sh
# 脚本语法 → dotnet build（警告即失败）→ 全量测试（大队 + 需独占的 git 用例分两段）→ 路径卫生
```

本仓库没有接 CI 服务，门禁是本地跑的；提交前手动执行即可。

跑导出类测试时若系统 `TMPDIR` 容量小，测试会降级到仓库内 `.hmi-test-tmp/`（已 gitignore）；
也可用 `HMI_TEST_TMPDIR` 显式指定一个**仓库外**的有空间目录 —— 指向仓库内部会让 git 工作流测试
把普通目录误判成仓库。

## 文档

| 文件 | 内容 |
|---|---|
| `docs/README.md` | 文档地图；代码注释里指向已移出专题的取回命令 |
| `docs/user-guide.md` | 用户手册：启动、设计、预览、导出、下发 |
| `docs/troubleshooting.md` | 故障排查：症状 → 原因 → 处置 → 依据 |

## 工程约定

- **包版本单一来源**：根 `Directory.Packages.props` 统一声明，`*.csproj` 里不写 `Version`。
- **编辑器版本单一来源**：`src/HmiEditor.Core/VersionInfo.cs`；major 固定 2，新功能 `2.x.0`、修缺陷 `2.x.y`。
- **编译期特性**：`src/` 与 `tests/` 各自的 `Directory.Build.props` 统一 `Nullable` / `ImplicitUsings` / `TargetFramework`；XAML 编译绑定显式开启（见 `src/Directory.Build.props` 的说明）。
- **设计常量单一来源**：颜色、尺寸、角度以 `src/HmiEditor.Core/Design/` 为准，不散写裸 RGB/hex。
- **生成代码断言**：用 `tests/HmiEditor.Core.Tests/Support/CSource.cs` 按调用点匹配，不写与排版绑定的整行 `Assert.Contains`。
- **测试临时目录**：用 `tests/HmiEditor.TestSupport/TestTempDir.cs`，不要直接调 `Path.GetTempPath()`。
- **注释与文档只写现状与为什么**：改了什么、何时改的归 commit message。

## 许可证

MIT，见 `LICENSE`。版权声明保留上游 `zhishu-hmi contributors` 原文 —— 那是法律声明，不是品牌位；
本仓库的品牌名统一为「晨歌 / chenge」。

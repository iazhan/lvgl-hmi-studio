# zhishu-hmi 故障排查手册

面向使用编辑器的现场工程师。
每条形如：**症状（你看到什么）→ 原因 → 处置（可执行命令/操作）→ 依据（仓库路径）**。
文中引用的错误文案均摘自源码，可直接搜索定位。

## 1. 构建与测试

### 1.1 `dotnet build` 报 SDK 版本不匹配 / 找不到 .NET 10

- **原因**：仓库用 `global.json` 固定 SDK 版本并要求 `rollForward: latestFeature`、不允许预览版。
- **处置**：在该目录执行 `dotnet --version` 核对，安装符合 `global.json` 的 .NET SDK 后重试。
- **依据**：`global.json`；各 `*.csproj` 的 `TargetFramework`。

### 1.2 测试写盘失败：`IOException: disk was full` / `No space left on device`

- **原因**：导出类测试会写出整套模拟器工程（`main.c`、`generated_*.c`、`CMakeLists.txt` 等），
  系统 `TMPDIR` 若指向小容量 tmpfs（例如 10 MB）就会写满。
- **处置**：把临时目录指到仓库外的大目录，例如
  `HMI_TEST_TMPDIR=/path/to/spacious/dir dotnet test HmiEditor.slnx -c Release`；
  缺省回退到仓库内 `.hmi-test-tmp/`（已 gitignore）。
- **注意**：**不要**指向本仓库内部——git 会向上发现仓库自身的 `.git`，使 Git 工作流测试把普通目录
  判成仓库、把历史读成父仓库的提交，表现为一堆看似产品缺陷的断言失败。
- **依据**：`README.md`「常见问题」、`tests/HmiEditor.TestSupport/TestTempDir.cs`。

### 1.3 `scripts/verify.sh` 中途因空间不足失败，报错点却看着无关

- **原因**：Golden 工程（含字体资源）与 LVGL 全量编译产物（数百 MB）都落在临时目录下。
- **处置**：脚本第 [6] 步自带早失败检查；按提示把 `TMPDIR` 指到仓库外 ≥1 GB 的目录：
  `TMPDIR=$HOME/.cache/zhishu-hmi-verify LVGL_DIR=~/Git/lvgl bash scripts/verify.sh`。
- **依据**：`scripts/verify.sh`；`docs/project-status.md`「复现命令」。

### 1.4 `scripts/ci-gate.sh` 报 `/dev/fd/63: 没有那个文件或目录`

- **症状**：门禁在「bash -n 脚本语法检查」前报 `/dev/fd/63` / `/dev/fd/62` 打不开。
- **原因**：这是**历史问题**——旧版脚本用进程替换 + `mapfile` 收集脚本清单，依赖 bash 的 `/dev/fd`；
  受限沙箱里会静默拿到空清单（假门禁）。现行脚本已改为 `mktemp` 临时文件 + 清单非空校验。
- **处置**：若仍复现，说明跑的是旧脚本或改动被回退：核对 `scripts/ci-gate.sh` 第 [1] 步是否为
  「临时文件收集 + `script_count -gt 0` 校验」；不要用「手工展开这一步」绕过门禁。
- **依据**：`scripts/ci-gate.sh` 第 [1] 步注释、`README.md`「常见问题」。

### 1.5 路径卫生门禁失败（硬编码开发机家目录）

- **症状**：门禁「路径卫生」步骤指出某文件里有被引号包裹的 `/home/<用户名>/...`。
- **原因**：产品/工具代码不得硬编码开发机家目录（会在他人机器上越界写盘、产物路径漂移、
  提示语复制粘贴即失败）。
- **处置**：改为相对路径 / 命令行参数 / 环境变量 / 从仓库相对定位；注释与文档里裸写路径允许。
- **依据**：`scripts/check-path-hygiene.sh`。

### 1.6 应用启动即崩且没有日志 / `AppBuilder` 报重复键

- **症状**：报 `ArgumentException: An item with the same key has already been added.
  Key: /Assets/Icons.axaml`。
- **原因**：Avalonia 资源目录里该键重复（同一个 `.axaml` 被同时当作 `AvaloniaResource` 与
  默认 glob 的 `AvaloniaXaml`）；增量构建把 XAML 编译步骤错开时可能残留。
- **处置**：对整个解决方案做一次干净构建（`dotnet build HmiEditor.slnx -c Release`），
  不要单独增量构建 `HmiEditor.App` 工程。
- **依据**：`src/HmiEditor.App/HmiEditor.App.csproj` 注释、
  `docs/2026-09-17-avalonia-resource-duplicate-key.md`；
  启动阶段日志位置见 `src/HmiEditor.App/Services/FileLogger.cs`（数据根下 `logs/`，
  数据根可用 `HMI_APP_DATA_ROOT` 覆盖，见 `src/HmiEditor.Core/Services/AppPaths.cs`）。

### 1.7 固件主机侧测试被跳过或失败

- **原因**：`run_host_tests.sh` 用 `gcc` 编译/运行 **9** 个主机侧步骤（前 8 步是 host 测试，
  第 9 步是 `lv_conf.h` 配置守卫）；无 gcc 时门禁默认跳过，
  CI 用 `CI_REQUIRE_FIRMWARE_TESTS=1` 转成失败。
- **处置**：安装 `gcc`；单独验证：`bash firmware/t113/tests/run_host_tests.sh`。
- **依据**：`README.md`、`scripts/ci-gate.sh` 第 **[7]** 步、`firmware/t113/tests/run_host_tests.sh`。

## 2. 导出与预览

### 2.1 「导出完成，但预览报没有 chenge-runtime 可执行文件」

- **原因**：导出预览要一份**宿主**运行时（`make HOST=1` 的产物），不是板端交叉编译产物。
- **处置**：在设置 → chenge-runtime 运行时目录填那个目录；或设环境变量 `CHENGE_RUNTIME_DIR`；
  或在 `chenge-runtime` 目录先 `make HOST=1`。
- **依据**：`src/HmiEditor.App/Services/ChengeExportService.cs` 的 `LocateRuntime`
  （顺序：设置 → 环境变量 → 安装目录内置 `runtime/`）。

### 2.2 预览窗口/截图是黑的，但没报错

- **原因**：无显示环境时 SDL 起不来；预览默认 `SDL_VIDEODRIVER=dummy` + `HMI_SHOT`，只出图。
- **处置**：看日志里有没有 `page: <id> 打开`；没有就是建页失败，按 2.3 查。
- **依据**：`ChengeExportService.PreviewAsync`。

### 2.3 「建页失败」/ 运行时拒绝载入

- **原因**：导出物不满足格式 0.6 的硬性约束（schema 外属性、动作缺 tag、
  报警/趋势组引用了不存在的名字、文本占位符写法不对）。
- **处置**：导出时会先给一份问题清单（`ChengeIssues`），按条目改；改完再来一次导出。
  回归可用运行时自带的 `chenge-runtime/tools/golden.sh`（54 例）。
- **依据**：`src/HmiEditor.Export.Chenge/ChengeExporter.cs`、`docs/chenge-export-format.md`。

### 2.4 导出的动作点了没反应

- **原因**：布尔变量的写动作必须发 `true` / `false`。发 `1` / `0` 会被运行时按
  「数值写布尔」拒绝（日志 `tagdb: tagN 是布尔，数值写被拒绝`）。
- **处置**：动作值填 `true` / `false`（或 1 / 0，导出器会按目标 tag 类型自动转 bool）。
- **依据**：`ChengeWidgetMapper.WriteValue`、运行时的 `chenge-runtime/src/core/tagdb.c`。

### 2.5 打开 `.hmi` 报「缺少 manifest.json」/「不支持的工程包格式版本」

- **原因**：文件不是有效工程包，或由更新版本编辑器创建（格式版本更高）。
- **处置**：确认文件来自本编辑器的「保存项目」；跨版本工程用创建它的编辑器打开。
- **依据**：`src/HmiEditor.Core/Serialization/HmiPackageService.cs` 的 `Unpack`。

## 3. 设备侧生效

### 3.1 拷了工程但板子还是老界面

- **原因**：`chenge-runtime` 启动时按 `HMI_PROJECT_DIR` 或 `/data/chenge/active` 里的名字定位工程，
  目录名写错就会继续用上一次的工程。
- **处置**：核对 `/data/chenge/projects/<名字>/manifest.json` 存在，`active` 文件里就是这个名字，
  然后 `killall chenge-runtime` 由 procd 拉起。
- **依据**：运行时的 `chenge-runtime/src/project/loader.c`（`HMI_PROJECT_DIR` / active 解析）。

### 3.2 「建页失败」出现在板端日志

- **原因**：板端字体/内存吃紧，或工程里某页控件数超过镜像上限（`core/limits.h` 的
  每页控件上限，编译期参数）。
- **处置**：先看失败原因那一行（哪个控件、哪个属性）；控件数超限要拆页。
- **依据**：运行时的 `chenge-runtime/src/ui/ui_page.c`、`chenge-runtime/src/core/caps.c`。

### 3.3 触摸、通讯与固件烧录类

这三类一律按固件文档排查，不在本文发明步骤：

- 触摸无反应 / 边缘点不到：`firmware/t113/docs/touch-hardware-diagnosis.md`、
  `firmware/t113/docs/flashing-guide.md` 第 7 节、`firmware/t113/docs/version-info.md`
  （触摸量程与 DTS 必须一致）、`firmware/t113/tina/dts/README.md`。
- Modbus / RS485 不通：先看 `firmware/t113/docs/flashing-guide.md` 第 7 节；连接参数可在编辑器
  「通讯配置」改完经「下载到 HMI」下发 `comm_config.ini` 免重编固件（格式与优先级见
  `firmware/t113/README.md`）。
- 固件烧录失败：`firmware/t113/docs/flashing-guide.md`（openixcli「Boot1 not found in firmware」
  的成因与 Boot0/Boot1 的 (maintype, subtype) 查找键、图像归一/自检脚本
  `tools/patch_image_boottype.py`（`--check`）、NAND 线刷、TF 卡启动、快捷部署
  `firmware/t113/scripts/deploy.sh`）、`firmware/t113/docs/wavepi-s6-flash-guide.md`
  （供电不足致 USB 传输超时、`Device reconnect failed`、刷回官方镜像 `flash_wavepi.sh --factory`）。

## 4. 已知限制（不要当成新缺陷反复排查）

### 4.1 Debug 全量并发下 `HmiEditor.App.UiTests` 的 testhost 会崩/挂死

- **症状**：`dotnet test HmiEditor.slnx`（Debug）跑到 UI 测试程序集时长时间无输出，或报
  「测试主机进程崩溃」；单独跑该项目却恒定全绿。
- **原因**：已定性为「进全量就出事」的间歇缺陷（复现率约 2/3），成因未定位；同一沙箱还存在一条
  与 headless 会话无关的失败入口（测试宿主根本起不来）。
- **处置**：门禁与端到端验收**只认 Release**（`scripts/ci-gate.sh` 与 `scripts/verify.sh`
  默认 `CONFIG=Release`）；不要拿 Debug 全量读数下结论。需要当场点名挂死测试时可加
  `--blame-hang --blame-hang-timeout 90s --blame-hang-dump-type none`（注意 blame 本身是混淆变量）。
- **依据**：`docs/project-status.md`「当前阻断项」第 12 条、
  `docs/2026-09-17-open-items-register.md` §5 第 27 项、
  `docs/2026-09-17-verify-end-to-end-revalidation.md`。

### 4.2 其余已知限制

| 说明（原因） | 处置 | 依据 |
| --- | --- | --- |
| Git 工作流用例全量并行偶发红：沙箱对「首次出现的文件路径」收约 60 ms 且全局串行，`git init` 撞穿 15 s 等待窗口。**（2026-09-27 阶段 115 又撞一次，指纹如下）**失败用例**耗时 50 s ≈ 3 × 15 s**，而断言落在**下游**那一条：`Commit_ThenHistory_ShowsEntry` 报 `Assert.NotEmpty() Failure: Collection was empty` （因为 `InitializeRepository` / `Commit` 的**返回值被测试丢弃** ⇒ 撞穿窗口这件事不报自己、只在下游显出「历史是空的」）。**证据**：同一批 8 条用例独立复跑 **5/5 绿 / 每次 241 ms**（`.workbuddy/tmp/s115-gitprobe1.log`）；同一棵树的门禁重跑，那一批 **551 ms**（`s115-gate1c.log`）⇒ **慢的那次与代码无关**，也不是门禁自身负载（两次门禁步 5 读数几乎相同） | 门禁已把 spawn git 的用例拆出单独跑（判据未放宽）；手工排查用 `--filter` 单独跑；⚠ **认这条指纹**：红的是非 `InitializeRepository*` 的同类用例也可以，只要「耗时 ≈ 15 s 的整数倍 ＋ 断言在下游」。**建议顺手改一处**（代价 2 行、不放开任何判据）：把那批用例的返回值从「丢弃」改成断言（`Assert.True(InitializeRepository(dir))` ＋ `var (ok, err) = Commit(...); Assert.True(ok, err)`）⇒ 失败消息就直接点名 `git 命令超时`，省掉一次 50 s 黑箱取证 | `scripts/ci-gate.sh` 第 [6] 步注释、`docs/2026-09-17-sandbox-newpath-serialization.md`；2026-09-27 那次现场：`.workbuddy/tmp/s115-gate1.log`（红，mtime `20:20:39`）／`s115-gate1b.log` ＋ `s115-gate1c.log`（绿，`20:25:13` / `20:29:51`） |
| `RuntimeConfig` 部分字段不走设备：`HistoryStoragePath`/`HeartbeatIntervalMs`/`MaxRuntimeTasks`/`EnableCrashDump`/`CrashDumpSector` 未下发，`EnableTamperDetection` 零消费，`WatchdogTimeoutMs`/`EnableHmiReboot` 是死字段（保留为老工程 JSON 兼容） | 在工程设置改这些项不会改变设备行为，闭环需先立机制 | `docs/2026-09-17-open-items-register.md` §3 第 9–11 项、`docs/2026-09-16-runtime-config-sinking.md` |
| 编辑器侧一键出发布包：`bash scripts/package-release.sh`（发布编辑器 + 生成版本清单与校验和）；出包后 `bash scripts/package-release.sh --verify <包目录>` 复核 | 出包步骤见包内 `UPGRADE.md` | `scripts/package-release.sh`、`docs/2026-09-18-release-packaging.md` |
| 极端迟到的设备回包仍可能串帧（晚于首字节宽限窗口） | 避免通道繁忙时下发超长命令；失败重试 | `docs/2026-09-17-open-items-register.md` §3 第 14 项、`docs/2026-09-16-channel-frame-completion-contract.md` |

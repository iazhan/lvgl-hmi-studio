# kinseal 板（Allwinner F1C100S）—— 编辑器侧目标事实

> 本文只记录**编辑器需要知道的板级事实**与每个声明值的出处；设备侧运行时的完整契约在姊妹仓库
> `f1c100s-project/chenge-rt/`（本文引用的路径均相对于该仓库根）。
> 目标插件实现：`src/Hmi.Hardware.Builtin/AllwinnerF1C100STarget.cs`（ID `f1c100s-kinseal`）。
> 测试：`tests/Hmi.Hardware.Tests/F1C100STargetTests.cs`。

## 1. 板子是什么

| 项 | 值 | 出处 |
| --- | --- | --- |
| SoC | Allwinner F1C100S（ARM926EJ-S / ARMv5TEJ，**无 FPU、无 GPU**） | datasheet；`chenge-rt` 构建用 buildroot `BR2_GCC_TARGET_CPU="arm926ej-s"` |
| 内存 | **32 MiB** 内置 DDR，无 swap（板上 `MemAvailable` 仅 11–13 MB） | `f1c100s-project/docs/kinseal/14` §资源账 |
| 显示 | **800×480** RGB666 面板，**倒装 180°**（内容侧旋转） | `chenge-rt/src/runtime/board_profile.h`（`HMI_LCD_WIDTH/HEIGHT`、`HMI_LCD_ROT180`） |
| 触摸 | 电阻屏（TPADC），`/dev/input/event0`；标定落 `/etc/hmi-cal` 六系数 | 同上；`chenge-rt/src/plat/board_fbdev.c` |
| 串口 | ttyS0 = 调试控制台（PE0/PE1）；ttyS1 = 第二串口（PE7/PE8） | `chenge-rt/src/runtime/board_profile.h` |
| 存储/启动 | **SD 卡启动**（u-boot + FAT boot 分区 + ext4 rootfs，分区大小随卡）；板载 NAND 128 MiB **未被使用** | `chenge-bsp/board/kinseal/genimage.cfg` |
| 485 | 模块电路级故障，不做方向控制 | `chenge-rt/src/runtime/board_profile.h` 尾注 |

## 2. 目标声明值 ↔ 证据对照

编辑器目标（`AllwinnerF1C100STarget`）的每个值都不是拍的，对照如下：

| 声明 | 值 | 证据（`f1c100s-project/` 内相对路径） |
| --- | --- | --- |
| `Metadata.Id` | `f1c100s-kinseal` | `chenge-rt/src/runtime/board_profile.h` 的 `HMI_BOARD_ID`（`hmi_runtime.h`：`HMI_TARGET_ID = HMI_BOARD_ID`） |
| `Metadata.Version` | `1.1.0` | `chenge-rt/src/runtime/hmi_runtime.h` 的 `HMI_RUNTIME_VERSION_STR` |
| `Display` | 800×480 / RGB666 / 仅 ARGB8888 | `chenge-rt/config/lv_conf.h`：`LV_COLOR_DEPTH 32`；`src/plat/board_fbdev.c`：fb `bpp=32` + `XRGB8888` |
| `Resources.RamSizeBytes` | 32 MiB | §1；仪表盘 RAM 轴按它判读 |
| `Resources.FlashSizeBytes` | **8 MiB** | `chenge-bsp/board/common/hmi/hmi_ota.sh`：`MAX_BYTES=8388608`。rootfs 分区 `size=0`（随卡）、NAND 未用 ⇒ 没有固定"板载 Flash 容量"，可取的是 OTA 对 `.so` 的硬门 |
| `Resources.RecommendedLvglHeapBytes` | 8 MiB | `chenge-rt/config/lv_conf.h`：`LV_MEM_SIZE (8U*1024U*1024U)` |
| `Resources.MaxTextureSize` | 800 | 屏宽；超过屏尺寸的纹理在 32 MiB 上纯属浪费 |
| `LvglConfig` | 32 位 / 9.5 / ABI 3 / 单缓冲 / FreeType + lodepng | `chenge-rt/src/runtime/hmi_ui_api.h`（`HMI_UI_LVGL_VERSION 0x905`、`HMI_UI_ABI_VERSION 3u`——宿主 dlopen 时严格比对，不符即拒载）；`config/lv_conf.h`（`LV_USE_FREETYPE 1`、`LV_USE_LODEPNG 1`、`LV_USE_LIBPNG 0`）；`board_fbdev.c`：`lv_display_set_buffers(..., NULL, ...)` = PARTIAL 单缓冲 |
| `Toolchain` | `arm-buildroot-linux-gnueabi-` / `-mcpu=arm926ej-s -mfloat-abi=soft` | `chenge-rt/Makefile` 板端 CC；buildroot `.config` 的 `BR2_GCC_TARGET_CPU` + gnueabi(soft-float) |
| `RecommendedInterfaces` / 驱动 | 仅 Serial / 仅 `modbus-rtu` | `chenge-rt/src/runtime/board_profile.h`（无网口/CAN；ttyS0 是控制台）；`chenge-rt/src/comm/modbus_rtu.c` 存在但 `HMI_WITH_COMM=0`，声明≠默认启用 |

## 3. 编辑器侧行为（本 fork 的默认值）

- **新建工程默认绑 kinseal**：`HardwareTargetId = f1c100s-kinseal`；屏幕默认模板 800×480
  （`ScreenDefaultsConfig` 默认值、`ScreenFactory` 兜底值、工程设置面板预设索引三处对齐，
  由 `F1C100STargetTests.EditorDefaults_AlignWithTheTargetDisplay` 钉住）。
- **资源仪表盘**：预算取自目标契约（RAM 32 MiB / Flash 8 MiB），判读阈值见 `ResourceBudget`。
- **工程设置 → 设备兼容性校验**：分辨率超过 800×480 报警告；工程 LVGL 主版本 ≠ 9 报**错误**
  （生成物会留着 9.x 已改名符号，板上编译不过）。
- T113 / RK3506 目标与 `firmware/t113/` 固件**保持原样**：kinseal 支持是增量，不占位、
  不改上游既有路径。

## 4. 边界：编辑器不管什么

设备侧运行时（fbdev/evdev/触摸标定/字体解析/OTA）在姊妹仓库，本仓库只到"生成 C 代码 + 编译成 `.so`"为止：

- **`.so` 的交叉编译**编辑器自己也能做（「编译转换」→ `F1C100SSoBuilder`：buildroot 工具链 +
  运行时契约头，产物 `libhmi_ui.so`）。它需要两样东西：buildroot 工具链、设备侧运行时目录
  （`src/runtime/*.h` + `config/lv_conf.h` + `third_party/lvgl-*`）。这两样**只从**
  ① 设置→真机部署 里显式指定的路径、② 环境变量 `F1C100S_GCC` / `CHENGE_RT_DIR`、
  ③ SDK（见下）来 —— 编辑器是用户单独安装的软件，**不假设**目标机上存在本项目的任何
  仓库布局（不向上遍历、不猜平级目录）。
- **SDK 的分发与安装**（默认随安装包，不依赖网络）：
  - 打包：`tools/package-kinseal-sdk.sh --runtime <chenge-rt> --host <buildroot/output/host> --slim`
    产 `sdk/kinseal-sdk-<ver>.tar.gz` + 同名 `.manifest.json`（含 SHA-256）。脚本末尾会用**包内**
    工具链真编一个 ARM `.so` 自检 —— 不可搬迁的工具链在这里就失败，不会等到用户装完。
    实测：`--slim` 后归档 **87 MB**、解压 **271 MB**（工具链 302 MB 里去掉 host python2.7 与
    `cc1plus`/`lto1`；C-only 够用）。
  - 随包：两个文件放 `<安装目录>/sdk/`，构建时由 `HmiEditor.App.csproj` 带上。归档而非展开目录，
    是因为 `tar` 能保可执行位（Windows 上直接铺文件会丢）。
  - 首次使用：`KinSealSdkBootstrapper` 校验清单后把归档装到 `<用户数据目录>/sdk/kinseal`
    （全有或全无：SHA 不符/内容不全一律不落盘），之后 `F1C100SSoBuilder` 自动认它。
  - 网络分发（**保留**，默认关闭）：设置→真机部署 填清单地址即改为按清单下载安装，
    与随包安装共用同一条校验/解压/落盘代码。
- 主仓库 `f1c100s-os/scripts/02_build.sh` §1.6 仍然保留同等的编译能力（chenge-rt Makefile）：
  没有 SDK 的机器上，导出源码交主仓库构建即可，两条路产物一致。
- **图片资产**：生成代码用 `IMG_PATH_<id>`（LVGL FS 按路径解码，PNG 走 lodepng）。板上加载需要
  主仓库构建时注入 `-DSIM_ASSET_DIR` 并部署资产目录——**该链路当前未接**（板上尚无工程图片），
  属主仓库侧工作。
- **字体**：工程字体经宿主 `hmi_resolve_font` 解析（FreeType 运行时 + 构建期子集 TTF），
  子集化在主仓库 `chenge-rt/tools/make_font_subset.py`。

## 5. 姊妹板预留

mcgs（同 SoC、面板正向装配）在 `chenge-rt` 里是同一 `board_profile.h` 的另一 profile
（`HMI_BOARD_MCGS=1`）。编辑器侧需要它时按同式新增一个目标（ID 建议对齐其 `HMI_BOARD_ID`），
不要往 kinseal 目标里塞开关——两块板的引脚/极性/标定结论不互通。

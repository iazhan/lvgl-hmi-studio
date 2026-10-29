# chenge-runtime —— 组态工程渲染运行时
#
# 与 chenge-config 共用同一棵 LVGL 9.5.0 源码树与同一份构建规则
# （chenge-common/common-lvgl.mk），但 lv_conf.h 与字体策略是本工程自己的
# 副本：config 与 runtime 是两个进程，各自编一份 LVGL 才能各自控制内存与
# 刷新节奏。
#
#   make HOST=1            宿主预览（SDL2 窗口，HMI_SHOT=a.bmp 可截图）
#   make HOST=0 ROT180=1   板端交叉（kinseal）；mcgs 用 ROT180=0 BOARD_MCGS=1
#   make EMBED=1          宿主嵌入版（libchenge_runtime.so，无窗口，帧交给调用方）
ifeq ($(WINDOWS),1)
  # Windows 隔离预览宿主（mingw-w64 交叉）。三条要点：
  #   · 必须同时给 HOST=1（common.mk 按它选 host 那支，否则会去拿 ARM 工具链）；
  #   · 产物带 .exe，目标文件另开目录 —— 与 Linux 的 .o 互不当命中（ABI 完全不同）；
  #   · 只编**预览宿主**：进程版 chenge-runtime 要 SDL2 窗口，不在这一步的范围里。
  HOST       := 1
  CC          := x86_64-w64-mingw32-gcc-posix
  AR          := x86_64-w64-mingw32-ar
  APP         := chenge-preview-host.exe
  OBJ_DIR     := objs-preview-win
  WIN_COMPAT  := win-compat
  # -static：把 libwinpthread/libgcc 一起链进去，安装包里只发一个 exe（否则用户机上
  #           少一个 libwinpthread-1.dll 就是"打不开，没有任何提示"）
  # -s      ：去掉调试符号；12.8MB → 2~3MB，安装包不该为符号表付钱
  LINK_FLAGS  := -static -s -Wl,--gc-sections
  # 强制包含兼容层：usleep/mkdir/setenv 这些"名字缺失"在一个地方补掉，
  # 源码里就不用为它们铺 #ifdef（两侧各写一套才会漂移）
  EXTRA_CFLAGS += -DHMI_HOST_BUILD -DHMI_WINDOWS=1 \
                  -I$(CURDIR)/$(WIN_COMPAT) -include $(CURDIR)/$(WIN_COMPAT)/win_compat.h
else ifeq ($(EMBED),1)
  APP        := libchenge_runtime.so
  LINK_FLAGS := -shared -Wl,-soname,libchenge_runtime.so
  OBJ_DIR    := objs-embed
  EXTRA_CFLAGS += -fPIC
else ifeq ($(PREVIEW),1)
  # 隔离预览宿主：同一批目标文件 + 自己的 main（src/preview_host.c），平台后端用 embed。
  APP        := chenge-preview-host
  OBJ_DIR    := objs-preview
  EXTRA_CFLAGS += -DHMI_HOST_BUILD
else
  # 默认产物：进程版（HOST=1 宿主 SDL / 板端 fbdev）。
  # ⚠ 这个 else 必须留着：APP 为空会让 `all:` 没有依赖，make 变成"无需做任何事"，
  # 于是改了源码却拿到上一次的二进制（版本号与行为都对不上，最难查）。
  APP        := chenge-runtime
endif
# 防线：APP 为空会让 `all:` 没有依赖，make 变成"无需做任何事"却返回 0 ——
# 改完源码、跑完"构建"，拿到的还是上一次的二进制（版本号与行为都对不上）。
ifeq ($(strip $(APP)),)
$(error APP 未定义：EMBED/PREVIEW 之外必须有一个默认产物名)
endif

HMI_ROOT := $(abspath $(dir $(lastword $(MAKEFILE_LIST)))/..)
include $(HMI_ROOT)/chenge-common/common.mk

# 界面引擎在 src/ui；工程解析在 src/project；变量表在 src/core；sim 在 src/comm。
# comm 下三份 sim 实现由能力开关二选一（见下），不参与通配。
# ⚠ 裁剪项必须写在**定义 SRCS 之前**、并且用 `=` 递归赋值保留：
#   · common-lvgl.mk 里的 `$(APP): $(OBJS)` 是**立即展开**的前置依赖，之后再改 SRCS
#     不会影响已声明的规则，被排除的文件照样会被编（这里踩过：改了 SRCS 仍在编
#     comm/modbus.c，然后死在 termios.h）；
#   · SRCS 一旦写成 `:=`，$(COMMON_SRCS)/$(PLAT_SRCS)/$(LVGL_SRCS) 就会被提前求值成空
#     （那时它们还没定义），链接期表现为"满屏 undefined reference lv_*"。
ifeq ($(WINDOWS),1)
  # 平台机制不同、PC 预览用不上的几个文件（各有空实现顶替，见 win-compat/）：
  #   main.c                 进程版入口（与 preview_host.c 抢 main）
  #   comm/modbus.c          socket + 独立线程的现场总线，PC 预览不连总线
  #   hmi_usb.c              U 盘挂载（sys/mount.h）
  #   hmi_project_import.c   从 U 盘导入工程（sys/statvfs.h）
  SRCS_EXCLUDE := src/main.c src/comm/modbus.c \
                  $(COMMON_DIR)/src/hmi_usb.c $(COMMON_DIR)/src/hmi_project_import.c
  SRCS_EXTRA   := $(WIN_COMPAT)/win_compat.c $(WIN_COMPAT)/board_stub_win.c \
                  $(WIN_COMPAT)/modbus_stub_win.c
endif

SRCS = $(filter-out $(SRCS_EXCLUDE), \
         $(wildcard src/*.c) $(wildcard src/core/*.c) $(wildcard src/project/*.c) \
         $(filter-out src/comm/sim.c src/comm/sim_cfg.c src/comm/sim_off.c, \
                      $(wildcard src/comm/*.c)) \
         $(wildcard src/ui/*.c) $(wildcard src/script/*.c) \
         $(wildcard src/data/*.c) \
         $(COMMON_SRCS) $(PLAT_SRCS) $(LVGL_SRCS)) \
       $(SRCS_EXTRA)

# Lua 5.4.7（37 §7.1）：只编核心与允许的库。linit.c 会 open 全部库（含 io/os/
# package/debug），liolib/loadlib/loslib 直接不编——沙箱库由 src/script/lua_rt.c 自建。
LUA_DIR := $(abspath third_party/lua-5.4.7/src)
LUA_SRCS := $(filter-out $(LUA_DIR)/lua.c $(LUA_DIR)/luac.c $(LUA_DIR)/onelua.c \
                          $(LUA_DIR)/linit.c $(LUA_DIR)/liolib.c \
                          $(LUA_DIR)/loadlib.c $(LUA_DIR)/loslib.c, \
              $(wildcard $(LUA_DIR)/*.c))
SRCS += $(LUA_SRCS)

# ── 能力开关（46 §1）──
# 只有真正带条件编译的模块才有开关，没编进来就等于没这个能力：caps 报 false、
# 装载期拒绝声明了该能力的工程。产品镜像关掉 sim（假设备与 sim.lua 不该出厂）。
# 开关与 spec/features.json 的 modules[].build 一一对应。
WITH_SIM ?= 1
ifeq ($(WITH_SIM),1)
  SRCS += src/comm/sim.c src/comm/sim_cfg.c
else
  SRCS += src/comm/sim_off.c
endif

# make 只按时间戳重编，看不见 -DHMI_WITH_* 变了。开关一变就让 caps.o 作废
# （它是唯一 #if HMI_WITH_SIM 的编译单元）；sim*.o 的增删由 SRCS 自己处理。
# 新增 #if HMI_WITH_* 的编译单元时，把它也加进这里的 rm 列表。
# 每页控件上限也是镜像级参数（core/limits.h），改它要重编 caps.o 才能反映到 --caps
WIDGETS_PER_PAGE ?= 150
CAPS_TAG := sim=$(WITH_SIM) wpp=$(WIDGETS_PER_PAGE)
$(shell printf '%s' '$(CAPS_TAG)' | cmp -s - .caps-tag 2>/dev/null || { \
        printf '%s' '$(CAPS_TAG)' > .caps-tag; rm -f objs/src/core/caps.o; })

# 未实现的模块：开关先占位，打开就报错，避免"配了开关以为能力已在"
NOT_READY := $(strip $(if $(filter 1,$(WITH_MQTT)),mqtt) $(if $(filter 1,$(WITH_MODBUS_TCP)),modbus_tcp) \
                      $(if $(filter 1,$(WITH_S7)),s7) $(if $(filter 1,$(WITH_ANIM)),anim) \
                      $(if $(filter 1,$(WITH_ANIM3)),anim3))
ifneq ($(NOT_READY),)
  $(error WITH_* 打开了尚未实现的模块：$(NOT_READY)（见 docs/chenge-rt/46 §2–§6，落地后才能生效）)
endif

include $(HMI_ROOT)/chenge-common/common-lvgl.mk

# 嵌入版把 main.c 排除掉：.so 里不该有自己的进程入口（宿主才是进程）。
# OBJS 在 common-lvgl.mk 里已展开，所以这里要跟着重算。
# 两个入口互斥：进程版要 main.c；隔离宿主只要 preview_host.c；.so 两个都不要
# （两个 main 同时进链接会直接报 multiple definition）。
ifeq ($(strip $(filter 1,$(PREVIEW) $(WINDOWS))),)
  # 进程版：要 main.c，不要 preview_host.c（两个入口同时进链接会 multiple definition）
  SRCS := $(filter-out src/preview_host.c,$(SRCS))
else
  SRCS := $(filter-out src/main.c,$(SRCS))
endif
ifeq ($(EMBED),1)
  SRCS := $(filter-out src/main.c,$(SRCS))
endif

ifneq ($(strip $(filter 1,$(EMBED) $(PREVIEW))),)
  EXTRA_CFLAGS += -DHMI_HOST_BUILD
endif

# SRCS 变过就要重算目标文件（common-lvgl.mk 里已按初始 SRCS 展开过一次）
OBJS := $(patsubst %.c,$(OBJ_DIR)/%.o,$(SRCS))

EXTRA_CFLAGS += -Iconfig -Iinclude -Isrc -Isrc/core -Isrc/project -Isrc/comm -Isrc/ui \
                -Isrc/script -Isrc/data -I$(LUA_DIR) \
                $(PKG_CFLAGS) -DHMI_LOG_TAG='"runtime"' -DHMI_WITH_SIM=$(WITH_SIM) \
                -DHMI_WIDGETS_PER_PAGE=$(WIDGETS_PER_PAGE)

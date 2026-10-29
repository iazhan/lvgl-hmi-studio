#!/usr/bin/env bash
# ============================================================================
# package.sh —— 一条命令出两个平台的可安装包（编辑器 + 内置运行时）
#
# 编 Ubuntu 版 → tar.gz 绿色版 + .deb 安装包
# 编 Windows 版 → zip 绿色版 + NSIS 安装包（在 Linux 上交叉编，不需要 Windows 机器）
#
# 用法：
#   bash tools/package.sh                        # 人机交互：问平台、问运行时目录
#   bash tools/package.sh --linux                # 只出 Linux 包
#   bash tools/package.sh --windows              # 只出 Windows 包
#   bash tools/package.sh --all --build-runtime <chenge-runtime 目录>
#                                                # 先 make 运行时（三份产物）再打包，最省事
#   bash tools/package.sh --all --runtime-linux <dir> --runtime-win <dir>
#                                                # 用现成的运行时（已编好的目录）
#
# 可选：--version 2.28.0（默认取 src/HmiEditor.Core/VersionInfo.cs）
#       --out <目录>（默认 dist）  --jobs N  --no-deb  --no-installer
#
# 运行时来源三选一：--build-runtime（现编）> --runtime-linux/--runtime-win（指定目录）
# > 本仓库 runtime/（自带兜底副本，tools/sync-runtime.sh 取的）。
# 脚本不去猜主仓库在哪：编辑器是独立安装的软件，猜路径的后果是
# "在作者机器上能打包、在别人机器上找不到"。
# ============================================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

VERSION=""
OUT="$ROOT/dist"
BUILD_RUNTIME=""
RUNTIME_LINUX=""
RUNTIME_WIN=""
DO_LINUX=0
DO_WIN=0
NO_DEB=0
NO_INSTALLER=0
JOBS="$(nproc 2>/dev/null || echo 4)"

usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d'; }

while [ $# -gt 0 ]; do
    case "$1" in
        --linux)          DO_LINUX=1 ;;
        --windows)        DO_WIN=1 ;;
        --all)            DO_LINUX=1; DO_WIN=1 ;;
        --version)        VERSION="${2:-}"; shift ;;
        --out)            OUT="${2:-}"; shift ;;
        --jobs)           JOBS="${2:-}"; shift ;;
        --build-runtime)  BUILD_RUNTIME="${2:-}"; shift ;;
        --runtime-linux)  RUNTIME_LINUX="${2:-}"; shift ;;
        --runtime-win)    RUNTIME_WIN="${2:-}"; shift ;;
        --no-deb)         NO_DEB=1 ;;
        --no-installer)   NO_INSTALLER=1 ;;
        -h|--help)        usage; exit 0 ;;
        *) echo "未知参数：$1（--help 看用法）" >&2; exit 2 ;;
    esac
    shift
done

step() { printf '\n=== %s ===\n' "$*"; }
die()  { echo "$*" >&2; exit 1; }

# ── 版本：唯一来源是 VersionInfo.cs，不在这里维护第二份 ──
if [ -z "$VERSION" ]; then
    VERSION="$(sed -n 's/.*public const string Version = "\([0-9][0-9.]*\)".*/\1/p' \
        "$ROOT/src/HmiEditor.Core/VersionInfo.cs" | head -1)"
    [ -n "$VERSION" ] || die "没能从 VersionInfo.cs 读出编辑器版本"
fi
printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$' \
    || die "版本号必须是 x.y.z（收到：$VERSION）"

# ── 平台：终端前问一句，管道/CI 里按参数走，不卡住 ──
if [ "$DO_LINUX" = 0 ] && [ "$DO_WIN" = 0 ]; then
    if [ -t 0 ]; then
        printf '打哪个平台？\n  1) Linux（.deb + tar.gz）\n  2) Windows（Setup.exe + zip）\n  3) 两个都打 [默认]\n> '
        read -r answer || answer=""
        case "${answer:-3}" in
            1) DO_LINUX=1 ;;
            2) DO_WIN=1 ;;
            *) DO_LINUX=1; DO_WIN=1 ;;
        esac
    else
        DO_LINUX=1
        DO_WIN=1
    fi
fi

# ── 运行时目录：给了 --build-runtime 就先编，编完直接用那个目录 ──
if [ -n "$BUILD_RUNTIME" ]; then
    [ -d "$BUILD_RUNTIME" ] || die "--build-runtime 不是目录：$BUILD_RUNTIME"
    [ -f "$BUILD_RUNTIME/Makefile" ] || die "$BUILD_RUNTIME 里没有 Makefile —— 这是 chenge-runtime 源码目录吗？"
    [ -z "$RUNTIME_LINUX$RUNTIME_WIN" ] \
        || die "--build-runtime 与 --runtime-linux/--runtime-win 二选一（编出来的就在源码目录里）"

    step "编运行时（$BUILD_RUNTIME，-j$JOBS）"
    # 画布/实时预览只认隔离预览宿主 —— 少了它编辑器装完也画不出图
    make -C "$BUILD_RUNTIME" HOST=1 PREVIEW=1 -j"$JOBS"
    if [ "$DO_LINUX" = 1 ]; then
        # 进程版（--shot 截图预览）要 SDL2 开发包；没有它就只能用隔离宿主截图
        if pkg-config --exists sdl2 2>/dev/null; then
            make -C "$BUILD_RUNTIME" HOST=1 -j"$JOBS"
        else
            echo "  跳过进程版 chenge-runtime：没有 SDL2 开发包（apt install libsdl2-dev）" >&2
        fi
        RUNTIME_LINUX="$BUILD_RUNTIME"
    fi
    if [ "$DO_WIN" = 1 ]; then
        command -v x86_64-w64-mingw32-gcc-posix >/dev/null 2>&1 \
            || die "缺 mingw-w64 交叉编译器（apt install gcc-mingw-w64-x86-64-posix）"
        make -C "$BUILD_RUNTIME" HOST=1 WINDOWS=1 -j"$JOBS"
        RUNTIME_WIN="$BUILD_RUNTIME"
    fi
fi

# 运行时目录解析：没给就用仓库自带的兜底副本，连它都没有才报错
need_runtime() {   # $1 = 用户给的值，$2 = 参数名，$3 = 仓库内兜底目录，$4 = 期望的可执行文件名
    local dir="${1:-$3}"
    [ -d "$dir" ] || die "运行时目录不存在：$dir（$2 可显式指定；或跑 tools/sync-runtime.sh 取一份进来）"
    [ -s "$dir/$4" ] || die "$dir 里没有 $4 —— 这个目录不是 $2 要的那个平台的产品"
    printf '%s' "$dir"
}

mkdir -p "$OUT"

step "版本 $VERSION ｜ 输出 $OUT"
if [ "$DO_LINUX" = 1 ]; then echo "  平台：Linux x86_64"; fi
if [ "$DO_WIN" = 1 ]; then echo "  平台：Windows x64"; fi

if [ "$DO_LINUX" = 1 ]; then
    RUNTIME_LINUX="$(need_runtime "$RUNTIME_LINUX" "--runtime-linux" \
        "$ROOT/runtime/linux-x64" "chenge-preview-host")"
    LIN_ARGS=(--runtime "$RUNTIME_LINUX" --version "$VERSION" --out "$OUT")
    if [ "$NO_DEB" = 1 ]; then LIN_ARGS+=(--no-deb); fi
    bash "$HERE/publish-linux.sh" "${LIN_ARGS[@]}"
fi

if [ "$DO_WIN" = 1 ]; then
    RUNTIME_WIN="$(need_runtime "$RUNTIME_WIN" "--runtime-win" \
        "$ROOT/runtime/win-x64" "chenge-preview-host.exe")"
    WIN_ARGS=(--runtime "$RUNTIME_WIN" --version "$VERSION" --out "$OUT")
    if [ "$NO_INSTALLER" = 1 ]; then WIN_ARGS+=(--no-installer); fi
    bash "$HERE/publish-win.sh" "${WIN_ARGS[@]}"
fi

# ── 交付清单：只列本轮真产出的文件，不写"应该有什么" ──
step "产物"
for f in \
    "$OUT/Chenge-HMI-Editor-${VERSION}-linux-x64.tar.gz" \
    "$OUT/chenge-hmi-editor_${VERSION}_amd64.deb" \
    "$OUT/Chenge-HMI-Editor-${VERSION}-win-x64.zip" \
    "$OUT/Chenge-HMI-Editor-Setup-${VERSION}.exe"
do
    [ -s "$f" ] || continue
    printf '  %-56s %6.1f MB\n' "$(basename "$f")" \
        "$(awk -v b="$(stat -c%s "$f")" 'BEGIN { print b/1048576 }')"
done
echo "  目录：$OUT"

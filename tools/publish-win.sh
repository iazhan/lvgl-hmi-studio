#!/usr/bin/env bash
# ============================================================================
# publish-win.sh —— 在 Linux 上出 Windows 包（与 publish-win.ps1 等价，给 CI/开发机用）
#
# 为什么要有 bash 版：编辑器可以**从 Linux 交叉发布**（dotnet publish -r win-x64），
# 运行时宿主的 .exe 也是 mingw 交叉编出来的（见主仓库 chenge-runtime 的
# `make HOST=1 WINDOWS=1`），NSIS 又有 Linux 版 ⇒ 整条出包链不需要 Windows 机器。
#
# 用法：
#   bash tools/publish-win.sh --runtime <chenge-runtime 目录> [--version 2.23.0]
#                             [--out dist] [--no-installer] [--zip-only]
#
# 产物：
#   <out>/win-x64/                                  自包含负载（编辑器 + 运行时子目录）
#   <out>/Chenge-HMI-Editor-<ver>-win-x64.zip       绿色版
#   <out>/Chenge-HMI-Editor-Setup-<ver>.exe         NSIS 安装包（有 makensis 时）
# ============================================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RUNTIME_DIR=""
VERSION="0.0.0"
OUT="$ROOT/dist"
MAKE_INSTALLER=1
MAKE_ZIP=1

while [ $# -gt 0 ]; do
    case "$1" in
        --runtime)       RUNTIME_DIR="${2:-}"; shift ;;
        --version)       VERSION="${2:-}"; shift ;;
        --out)           OUT="${2:-}"; shift ;;
        --no-installer)  MAKE_INSTALLER=0 ;;
        --zip-only)      MAKE_INSTALLER=0 ;;
        -h|--help)       sed -n '2,/^set -euo/p' "$0" | sed '$d'; exit 0 ;;
        *) echo "未知参数：$1" >&2; exit 2 ;;
    esac
    shift
done

# 不传 --runtime 就用仓库里自带的兜底副本（tools/sync-runtime.sh 取的）
if [ -z "$RUNTIME_DIR" ]; then
    RUNTIME_DIR="$ROOT/runtime/win-x64"
    echo "运行时：$RUNTIME_DIR（仓库内兜底；要换成自己编的用 --runtime <目录>）"
fi
if [ ! -d "$RUNTIME_DIR" ]; then
    echo "运行时目录不存在：$RUNTIME_DIR" >&2
    exit 2
fi

PAYLOAD="$OUT/win-x64"
RT_TARGET="$PAYLOAD/chenge-runtime"

step() { printf '\n== %s ==\n' "$*"; }

step "1/4 校验运行时（$RUNTIME_DIR）"
for f in chenge-preview-host.exe; do
    [ -s "$RUNTIME_DIR/$f" ] || { echo "缺少 $f —— 先在 chenge-runtime 里 make HOST=1 WINDOWS=1" >&2; exit 1; }
done
# 进程版是可选的；带了它就必须带 SDL2.dll（否则装完"导出并预览"起不来）
if [ -s "$RUNTIME_DIR/chenge-runtime.exe" ] && [ ! -s "$RUNTIME_DIR/SDL2.dll" ]; then
    echo "注意：带上了 chenge-runtime.exe 但没有 SDL2.dll，进程版会起不来（建议别放它）" >&2
fi

step "2/4 dotnet publish（win-x64 自包含）"
rm -rf "$PAYLOAD"
dotnet publish "$ROOT/src/HmiEditor.App/HmiEditor.App.csproj" \
    -c Release -r win-x64 --self-contained true \
    -p:Version="$VERSION" -p:InformationalVersion="$VERSION" \
    -o "$PAYLOAD" --nologo -v q

step "3/4 拷运行时"
mkdir -p "$RT_TARGET"
# 白名单拷贝：--runtime 常指向 chenge-runtime 源码目录，那里还有 objs*/src/third_party
# （实测 150 MB 构建中间物）。全拷会把安装包撑大三倍，编辑器却只需要二进制与 sample
# （sample 是 --selftest-host 的默认工程）。
for f in chenge-preview-host.exe chenge-runtime.exe SDL2.dll; do
    if [ -s "$RUNTIME_DIR/$f" ]; then cp -a "$RUNTIME_DIR/$f" "$RT_TARGET/"; fi
done
if [ -d "$RUNTIME_DIR/sample" ]; then cp -a "$RUNTIME_DIR/sample" "$RT_TARGET/"; fi
bash "$HERE/verify-win-payload.sh" "$PAYLOAD"

step "4/4 打包"
if [ "$MAKE_ZIP" = 1 ]; then
    ZIP="$OUT/Chenge-HMI-Editor-$VERSION-win-x64.zip"
    rm -f "$ZIP"
    ( cd "$PAYLOAD" && zip -qr "$ZIP" . )
    printf '  zip   %s（%.1f MB）\n' "$ZIP" "$(stat -c%s "$ZIP" | awk '{print $1/1048576}')"
fi

if [ "$MAKE_INSTALLER" = 1 ]; then
    if command -v makensis >/dev/null 2>&1; then
        SETUP="$OUT/Chenge-HMI-Editor-Setup-$VERSION.exe"
        rm -f "$SETUP"
        ( cd "$HERE/installer" && makensis -V2 -DVERSION="$VERSION" -DPAYLOAD="$PAYLOAD" -DOUTDIR="$OUT" hmi-editor.nsi )
        printf '  setup %s（%.1f MB）\n' "$SETUP" \
            "$(stat -c%s "$SETUP" | awk '{print $1/1048576}')"
    else
        echo "  跳过安装包：没有 makensis（apt install nsis / 或 Windows 上装 NSIS）" >&2
    fi
fi

echo
echo "完成：$OUT"

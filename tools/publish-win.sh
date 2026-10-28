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
#   <out>/win-x64/                            自包含负载（编辑器 264 文件 + 运行时子目录）
#   <out>/HMI-Studio-<ver>-win-x64.zip        绿色版
#   <out>/HMI-Studio-Setup-<ver>.exe          NSIS 安装包（有 makensis 时）
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
        -h|--help)       sed -n '2,20p' "$0"; exit 0 ;;
        *) echo "未知参数：$1" >&2; exit 2 ;;
    esac
    shift
done

if [ -z "$RUNTIME_DIR" ] || [ ! -d "$RUNTIME_DIR" ]; then
    echo "缺 --runtime <目录>：Windows 版运行时目录（含 chenge-preview-host.exe）" >&2
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
cp -a "$RUNTIME_DIR"/. "$RT_TARGET"/
# 构建中间物不进安装包（源码/目标文件会把包撑大，也容易让人以为"源码随包"）
find "$RT_TARGET" -maxdepth 2 -type f \( -name '*.o' -o -name '*.d' -o -name '*.c' -o -name '*.h' \) -delete
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
        ( cd "$HERE/installer" && makensis -V2 -DVERSION="$VERSION" -DPAYLOAD="$PAYLOAD" hmi-editor.nsi )
        printf '  setup %s（%.1f MB）\n' "$OUT/Chenge-HMI-Editor-Setup-$VERSION.exe" \
            "$(stat -c%s "$OUT/Chenge-HMI-Editor-Setup-$VERSION.exe" | awk '{print $1/1048576}')"
    else
        echo "  跳过安装包：没有 makensis（apt install nsis / 或 Windows 上装 NSIS）" >&2
    fi
fi

echo
echo "完成：$OUT"

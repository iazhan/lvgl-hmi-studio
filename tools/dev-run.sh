#!/usr/bin/env bash
# ============================================================================
# dev-run.sh —— 编译并启动编辑器（开发用）
#
# 与 tools/package.sh 的分工：这个只管「改完代码 → 编出来 → 跑起来看」；
# 要出安装包走 tools/package.sh。
#
# 用法：
#   bash tools/dev-run.sh                     # Debug 编译 + 启动
#   bash tools/dev-run.sh --release           # Release 编译 + 启动
#   bash tools/dev-run.sh --no-build          # 跳过编译，直接用上次的产物
#   bash tools/dev-run.sh --clean             # 先 dotnet clean 再编
#   bash tools/dev-run.sh -- --selftest-host  # 「--」之后的参数原样传给编辑器
#   bash tools/dev-run.sh -- ~/proj/demo.hmi  # 启动后直接打开这个工程
#
# 运行时目录：没设 CHENGE_RUNTIME_DIR 时用仓库内兜底副本 runtime/linux-x64。
# 要用自己编的运行时，先 export CHENGE_RUNTIME_DIR=<目录>（脚本不覆盖已有值）。
#
# 编辑器自己认的参数（`--` 之后）：--selftest-embed / --selftest-host / --mem / --iso，
# 以及一个位置参数＝工程路径。不带参数就是打开引导页。
# ============================================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
PROJ="$ROOT/src/HmiEditor.App/HmiEditor.App.csproj"

CONFIG=Debug
DO_BUILD=1
DO_CLEAN=0
PASSTHRU=()

while [ $# -gt 0 ]; do
    case "$1" in
        --release)  CONFIG=Release ;;
        --debug)    CONFIG=Debug ;;
        --no-build) DO_BUILD=0 ;;
        --clean)    DO_CLEAN=1 ;;
        -h|--help)  sed -n '2,/^set -euo/p' "$0" | sed '$d'; exit 0 ;;
        --)         shift; PASSTHRU=("$@"); break ;;
        *)          echo "未知参数：$1（编辑器自己的参数请放在 -- 之后）" >&2; exit 2 ;;
    esac
    shift
done

[ -f "$PROJ" ] || { echo "找不到项目文件：$PROJ" >&2; exit 2; }

# 运行时：只补默认值，不覆盖用户显式设置
if [ -z "${CHENGE_RUNTIME_DIR:-}" ] && [ -d "$ROOT/runtime/linux-x64" ]; then
    CHENGE_RUNTIME_DIR="$ROOT/runtime/linux-x64"
    export CHENGE_RUNTIME_DIR
    echo "运行时：$CHENGE_RUNTIME_DIR（仓库内兜底，要换就 export CHENGE_RUNTIME_DIR）"
fi

if [ "$DO_CLEAN" = 1 ]; then
    echo "== dotnet clean ($CONFIG) =="
    dotnet clean "$PROJ" -c "$CONFIG" --nologo -v q
fi

if [ "$DO_BUILD" = 1 ]; then
    echo "== dotnet build ($CONFIG) =="
    dotnet build "$PROJ" -c "$CONFIG" --nologo -v q
fi

# 没有图形会话时提前说清楚，免得 Avalonia 抛一句看不懂的初始化失败
if [ -z "${DISPLAY:-}" ] && [ -z "${WAYLAND_DISPLAY:-}" ]; then
    echo "注意：没有 DISPLAY / WAYLAND_DISPLAY —— 窗口起不来。" \
         "无头自检请用：bash tools/dev-run.sh -- --selftest-host" >&2
fi

echo "== 启动编辑器 =="
exec dotnet run --project "$PROJ" -c "$CONFIG" --no-build -- "${PASSTHRU[@]}"

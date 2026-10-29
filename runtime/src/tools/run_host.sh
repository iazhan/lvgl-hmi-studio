#!/bin/sh
# run_host.sh — 在宿主开窗口跑一个组态工程（人工验收用）
#
# 用法：
#   tools/run_host.sh                 列出 spec/golden 下的工程，选一个跑
#   tools/run_host.sh g02             按名字跑（前缀匹配）
#   tools/run_host.sh /path/to/proj   按路径跑
#
# 环境变量：
#   DISPLAY         X 显示（无头环境用 SDL_VIDEODRIVER=dummy 自己指定）
#   HMI_PORT_COM1   Modbus 工程联调时指向 pty 或串口（如 /dev/pts/5、/dev/ttyUSB1）
#   HMI_DATA_DIR    趋势数据根目录；缺省用 $ROOT/.hmi-data（重启后历史还在）
#
# 非交互（stdin 不是终端）时不提问，直接跑第一个工程——脚本化调用不会卡住。

set -u

HERE=$(cd "$(dirname "$0")" && pwd)
RT=$(dirname "$HERE")
ROOT=$(cd "$RT/../.." && pwd)
BIN="$RT/chenge-runtime"
GOLDEN="$ROOT/spec/golden"

if [ ! -x "$BIN" ]; then
    echo "!! 找不到 $BIN，先执行：cd chenge-hmi/chenge-runtime && make HOST=1"
    exit 1
fi

pick() {
    # 输出工程目录列表（每行一个）
    for d in "$GOLDEN"/*/; do
        [ -f "$d/manifest.json" ] && printf '%s\n' "${d%/}"
    done
}

arg="${1:-}"
if [ -n "$arg" ]; then
    if [ -d "$arg" ] && [ -f "$arg/manifest.json" ]; then
        proj=$arg
    else
        proj=""
        for d in $(pick); do
            case "$(basename "$d")" in
            "$arg"*) proj=$d; break ;;
            esac
        done
        if [ -z "$proj" ]; then
            echo "!! 没有匹配 '$arg' 的工程。可选："
            for d in $(pick); do echo "   $(basename "$d")"; done
            exit 1
        fi
    fi
else
    n=0
    list=""
    for d in $(pick); do
        n=$((n + 1))
        list="$list$n:$d\n"
        printf '%2d) %s\n' "$n" "$(basename "$d")"
    done
    [ "$n" -gt 0 ] || { echo "!! $GOLDEN 下没有工程"; exit 1; }

    if [ -t 0 ]; then
        printf '选择 [1-%d，回车默认 1]: ' "$n"
        read -r sel || true
        [ -n "${sel:-}" ] || sel=1
    else
        sel=1
    fi
    case "$sel" in
    *[!0-9]*|"") echo "!! 选择必须是数字"; exit 1 ;;
    esac
    [ "$sel" -ge 1 ] && [ "$sel" -le "$n" ] || { echo "!! 超出范围"; exit 1; }
    proj=$(printf "$list" | sed -n "${sel}p" | cut -d: -f2-)
fi

echo "工程: $proj"
[ -n "${HMI_PORT_COM1:-}" ] && echo "com1: $HMI_PORT_COM1"
[ -z "${DISPLAY:-}" ] && [ -z "${WAYLAND_DISPLAY:-}" ] && [ -z "${SDL_VIDEODRIVER:-}" ] && {
    echo "!! 没有 DISPLAY；无头环境请设 SDL_VIDEODRIVER=dummy（或指定 DISPLAY=:0）"
    exit 1
}

# 趋势数据固定放在仓库根的 .hmi-data：换工程、重启窗口，历史都还在
HMI_DATA_DIR="${HMI_DATA_DIR:-$ROOT/.hmi-data}"
export HMI_DATA_DIR

HMI_PROJECT_DIR="$proj" exec "$BIN"

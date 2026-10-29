#!/usr/bin/env bash
# ============================================================================
# sync-runtime.sh —— 把一份 chenge-runtime 取进本仓库 runtime/（打包的兜底来源）
#
# 为什么要有仓库内副本：打包脚本若只能靠 --runtime 指到主仓库，换机器/主仓库被挪走
# 就出不了包。本仓库 runtime/ 自带一份产物，tools/package.sh 不传 --runtime 时用它。
#
# 取什么（只要产物）：
#   runtime/linux-x64/   chenge-preview-host、chenge-runtime、libchenge_runtime.so、sample/
#   runtime/win-x64/     chenge-preview-host.exe、chenge-runtime.exe、SDL2.dll、sample/
#
# 源码不进来：它依赖主仓库的 chenge-common 与 LVGL 树，搬过来也编不了；
# 放在这里只会让人以为本仓库能重建运行时。要改运行时去主仓库改、编完再同步产物。
#
# 用法：
#   bash tools/sync-runtime.sh --from <主仓库>/chenge-hmi/chenge-runtime
#   bash tools/sync-runtime.sh --from <...> --platform linux     # 只取一个平台
# ============================================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
DEST="$ROOT/runtime"
FROM=""
PLATFORM="all"

while [ $# -gt 0 ]; do
    case "$1" in
        --from)       FROM="${2:-}"; shift ;;
        --platform)   PLATFORM="${2:-}"; shift ;;
        -h|--help)    sed -n '2,/^set -euo/p' "$0" | sed '$d'; exit 0 ;;
        *) echo "未知参数：$1" >&2; exit 2 ;;
    esac
    shift
done

[ -n "$FROM" ] || { echo "缺 --from <chenge-runtime 目录>" >&2; exit 2; }
[ -d "$FROM" ] || { echo "目录不存在：$FROM" >&2; exit 2; }

TOOK=0
step() { printf '\n== %s ==\n' "$*"; }
copy_platform() {   # $1 = linux-x64 | win-x64 ; 其余参数 = 要取的文件名
    local dir="$1"; shift
    local target="$DEST/$dir"
    local names=("$@")
    local found=0
    rm -rf "$target"
    mkdir -p "$target"
    for f in "${names[@]}"; do
        if [ -s "$FROM/$f" ]; then
            cp -a "$FROM/$f" "$target/"
            found=$((found + 1))
            printf '  %s（%.1f MB）\n' "$f" \
                "$(awk -v b="$(stat -c%s "$FROM/$f")" 'BEGIN { print b/1048576 }')"
        fi
    done
    if [ -d "$FROM/sample" ]; then cp -a "$FROM/sample" "$target/"; fi
    if [ "$found" -eq 0 ]; then
        rmdir "$target"
        echo "  跳过 $dir：源目录里这些文件一个都没有（$* ）" >&2
        return 1
    fi
    return 0
}

mkdir -p "$DEST"

if [ "$PLATFORM" = all ] || [ "$PLATFORM" = linux ]; then
    step "Linux x86_64 产物"
    if copy_platform linux-x64 chenge-preview-host chenge-runtime libchenge_runtime.so; then TOOK=1; fi
fi
if [ "$PLATFORM" = all ] || [ "$PLATFORM" = win ]; then
    step "Windows x64 产物"
    if copy_platform win-x64 chenge-preview-host.exe chenge-runtime.exe SDL2.dll; then TOOK=1; fi
fi
[ "$TOOK" = 1 ] || { echo "什么都没取到：$FROM 里没有这两个平台的产物（先编运行时）" >&2; exit 1; }

# ── 版本与来源：两份文字记录是"这份二进制到底哪来的"唯一凭据 ──
VERSION="$(cat "$FROM/VERSION" 2>/dev/null | tr -d '[:space:]')"
[ -n "$VERSION" ] || VERSION="unknown"
printf '%s\n' "$VERSION" > "$DEST/VERSION"

COMMIT="$(git -C "$FROM" log -1 --format='%h %s' 2>/dev/null || true)"
cat > "$DEST/SOURCE.txt" <<SOURCE
来源目录：$FROM
运行时版本：$VERSION
取用时间：$(date '+%Y-%m-%d %H:%M:%S')
来源提交：${COMMIT:-（该目录不在 git 工作区，未记录）}

更新方式（在主仓库里先编出产物，再取一份过来）：
  make -C <主仓库>/chenge-hmi/chenge-runtime PREVIEW=1 HOST=1    # 画布宿主
  make -C <主仓库>/chenge-hmi/chenge-runtime HOST=1              # 截图预览的进程版
  make -C <主仓库>/chenge-hmi/chenge-runtime HOST=1 WINDOWS=1    # Windows 宿主（mingw-w64）
  bash tools/sync-runtime.sh --from <主仓库>/chenge-hmi/chenge-runtime
SOURCE

step "完成"
printf '  runtime/ 共 %s（版本 %s）\n' "$(du -sh "$DEST" | cut -f1)" "$VERSION"

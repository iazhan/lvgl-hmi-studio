#!/usr/bin/env bash
# ============================================================================
# verify-win-payload.sh —— 检查一份 Windows 负载「装完能不能直接用」
#
# 编辑器找运行时的顺序（ChengeExportService.LocateRuntime）：
#   设置项 → 环境变量 CHENGE_RUNTIME_DIR → <安装目录>/chenge-runtime → <安装目录>
# 安装包只用第三条，所以这里判的也是第三条：所有东西都必须在**同一个安装目录**下。
#
# 用法：
#   bash tools/verify-win-payload.sh <dist/win-x64>
#   bash tools/verify-win-payload.sh dist/win-x64 --allow-missing-runtime   # 只验编辑器
#
# 退出码：0 = 可以出包；1 = 缺东西（清单见输出）。
# ============================================================================
set -uo pipefail

PAYLOAD="${1:-}"
ALLOW_MISSING=${2:-}
if [ -z "$PAYLOAD" ]; then
    echo "用法：bash tools/verify-win-payload.sh <dist/win-x64> [--allow-missing-runtime]" >&2
    exit 2
fi
if [ ! -d "$PAYLOAD" ]; then
    echo "目录不存在：$PAYLOAD" >&2
    exit 2
fi

fail=0
ok()   { printf '  ok    %s\n' "$1"; }
bad()  { printf '  MISS  %s\n' "$1"; fail=$((fail + 1)); }

check_file() {   # $1 = 相对路径，$2 = 说明
    local p="$PAYLOAD/$1"
    if [ -f "$p" ] && [ -s "$p" ]; then
        ok "$1（$(stat -c%s "$p" | awk '{printf "%.1f MB", $1/1048576}')）— $2"
    else
        bad "$1 — $2"
    fi
}

echo "负载：$PAYLOAD"
check_file "HmiEditor.App.exe" "编辑器入口（win-x64 自包含，目标机不需要装 .NET）"

echo "随包运行时（装完不填路径就能画布/预览）："
for f in "chenge-runtime/chenge-preview-host.exe:必需 —— 设计画布 / 实时预览 / 截图预览都跑它（不链 SDL）"; do
    rel="${f%%:*}"; why="${f#*:}"
    if [ -f "$PAYLOAD/$rel" ] && [ -s "$PAYLOAD/$rel" ]; then
        ok "$rel（$(stat -c%s "$PAYLOAD/$rel" | awk '{printf "%.1f MB", $1/1048576}')）— $why"
    else
        if [ "$ALLOW_MISSING" = "--allow-missing-runtime" ]; then
            printf '  skip  %s — %s（--allow-missing-runtime）\n' "$rel" "$why"
        else
            bad "$rel — $why"
        fi
    fi
done

# 可选：进程版运行时（需要 SDL2.dll 配套，缺一个就别说"两个都在"）
if [ -f "$PAYLOAD/chenge-runtime/chenge-runtime.exe" ] && [ ! -f "$PAYLOAD/chenge-runtime/SDL2.dll" ]; then
    bad "chenge-runtime/chenge-runtime.exe 在但 SDL2.dll 不在 —— 进程版起不来（不想要它就别放这个 exe）"
fi

# 运行时的依赖不该混进安装包（构建中间物会把包撑大，也会让人以为"源码随包"）
junk=$(find "$PAYLOAD/chenge-runtime" -maxdepth 2 -type f \( -name '*.o' -o -name '*.d' -o -name '*.c' \) 2>/dev/null | head -5)
[ -n "$junk" ] && printf '  warn  运行时目录里混进了构建中间物：\n%s\n' "$(echo "$junk" | sed 's/^/         /')"

# 两个 exe 必须在同一个目录树里：编辑器只按"安装目录"找，不接受平级
if [ -f "$PAYLOAD/chenge-runtime/chenge-preview-host.exe" ] && [ ! -f "$PAYLOAD/HmiEditor.App.exe" ]; then
    bad "运行时在、编辑器不在同一目录 —— 布局不对"
fi

echo
if [ "$fail" -gt 0 ]; then
    echo "不可出包：$fail 项缺失。"
    echo "运行时的两个 .exe 与 SDL2.dll 必须是 **Windows 版**（C + SDL2，在 MSYS2/mingw-w64 里编）。"
    exit 1
fi
echo "可以出包（tools/publish-win.ps1 已产出 zip；有 NSIS 时同目录还有 Setup.exe）。"

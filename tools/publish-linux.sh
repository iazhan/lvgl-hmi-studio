#!/usr/bin/env bash
# ============================================================================
# publish-linux.sh —— 出 Linux（Ubuntu x86_64）包：编辑器 + 随包运行时
#
# 与 publish-win.sh 对称；总入口是 tools/package.sh，本脚本可单独调。
#
# 负载布局（编辑器按这个顺序找运行时，见 ChengeExportService.LocateRuntime）：
#   <负载>/HmiEditor.App                       编辑器（.NET 自包含，目标机不需装 .NET）
#   <负载>/chenge-hmi-editor                   启动器（从任意目录启动都能找到随包文件）
#   <负载>/chenge-runtime/chenge-preview-host  设计画布 / 实时预览的宿主（必需）
#   <负载>/chenge-runtime/chenge-runtime       截图预览的进程版（可选，要 SDL2）
#
# 用法：
#   bash tools/publish-linux.sh --runtime <chenge-runtime 目录> [--version 2.27.0]
#                               [--out dist] [--no-tar] [--no-deb]
#
# 产物：
#   <out>/linux-x64/                              自包含负载（装完即用）
#   <out>/Chenge-HMI-Editor-<ver>-linux-x64.tar.gz  绿色版（解压即用）
#   <out>/chenge-hmi-editor_<ver>_amd64.deb        安装包（/opt + 应用菜单 + 命令）
# ============================================================================
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RUNTIME_DIR=""
VERSION="0.0.0"
OUT="$ROOT/dist"
MAKE_TAR=1
MAKE_DEB=1

while [ $# -gt 0 ]; do
    case "$1" in
        --runtime)  RUNTIME_DIR="${2:-}"; shift ;;
        --version)  VERSION="${2:-}"; shift ;;
        --out)      OUT="${2:-}"; shift ;;
        --no-tar)   MAKE_TAR=0 ;;
        --no-deb)   MAKE_DEB=0 ;;
        -h|--help)  sed -n '2,/^set -euo/p' "$0" | sed '$d'; exit 0 ;;
        *) echo "未知参数：$1" >&2; exit 2 ;;
    esac
    shift
done

# 不传 --runtime 就用仓库里自带的兜底副本（tools/sync-runtime.sh 取的）
if [ -z "$RUNTIME_DIR" ]; then
    RUNTIME_DIR="$ROOT/runtime/linux-x64"
    echo "运行时：$RUNTIME_DIR（仓库内兜底；要换成自己编的用 --runtime <目录>）"
fi
if [ ! -d "$RUNTIME_DIR" ]; then
    echo "运行时目录不存在：$RUNTIME_DIR" >&2
    exit 2
fi
if ! printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    # deb 的 Version 与 NSIS 的 VIProductVersion 都要求纯数字点分；带后缀的版本号到打包最后一步才炸
    echo "版本号必须是 x.y.z（收到：$VERSION）" >&2
    exit 2
fi

PAYLOAD="$OUT/linux-x64"
RT_TARGET="$PAYLOAD/chenge-runtime"
STAGE_NAME="Chenge-HMI-Editor-${VERSION}-linux-x64"

step() { printf '\n== %s ==\n' "$*"; }
mb()   { awk -v b="$1" 'BEGIN { printf "%.1f MB", b/1048576 }'; }
size() { stat -c%s "$1"; }

# ── 0. 运行时必须是 Linux 版：拿 Windows 目录来会装出"打不开、没提示"的包 ──
step "1/5 校验运行时（$RUNTIME_DIR）"
HOST_BIN="$RUNTIME_DIR/chenge-preview-host"
[ -s "$HOST_BIN" ] || {
    echo "缺少 $HOST_BIN —— 先在 chenge-runtime 里 make PREVIEW=1 HOST=1" >&2
    exit 1
}
if command -v file >/dev/null 2>&1 && ! file -b "$HOST_BIN" | grep -q 'ELF 64-bit'; then
    echo "$HOST_BIN 不是 Linux ELF —— 这是 Windows 版运行时吗？" >&2
    exit 1
fi

step "2/5 dotnet publish（linux-x64 自包含）"
rm -rf "$PAYLOAD"
# 首次发布会从 nuget 取 runtime pack（约 30 MB）；之后走本地缓存
dotnet publish "$ROOT/src/HmiEditor.App/HmiEditor.App.csproj" \
    -c Release -r linux-x64 --self-contained true \
    -p:Version="$VERSION" -p:InformationalVersion="$VERSION" \
    -o "$PAYLOAD" --nologo -v q

step "3/5 拷运行时 + 启动器"
mkdir -p "$RT_TARGET"
# 白名单拷贝：--runtime 常指向 chenge-runtime 源码目录，那里还有 objs*/src/third_party
# （实测 150 MB 构建中间物）。全拷会把包撑大三倍，编辑器却只需要二进制与 sample
# （sample 是 --selftest-host 的默认工程）。
for f in chenge-preview-host chenge-runtime libchenge_runtime.so; do
    if [ -s "$RUNTIME_DIR/$f" ]; then cp -a "$RUNTIME_DIR/$f" "$RT_TARGET/"; fi
done
if [ -d "$RUNTIME_DIR/sample" ]; then cp -a "$RUNTIME_DIR/sample" "$RT_TARGET/"; fi

cat > "$PAYLOAD/chenge-hmi-editor" <<'LAUNCHER'
#!/bin/sh
# 先切到程序目录：随包运行时按"编辑器所在目录/chenge-runtime"查找
self=$(readlink -f "$0" 2>/dev/null || echo "$0")
cd "$(dirname "$self")" || exit 1
exec ./HmiEditor.App "$@"
LAUNCHER
chmod +x "$PAYLOAD/chenge-hmi-editor"
chmod +x "$PAYLOAD/HmiEditor.App"

step "4/5 自查负载"
fail=0
[ -s "$PAYLOAD/HmiEditor.App" ] || { echo "  MISS HmiEditor.App" >&2; fail=$((fail + 1)); }
[ -x "$PAYLOAD/HmiEditor.App" ] || { echo "  可执行位丢了：HmiEditor.App" >&2; fail=$((fail + 1)); }
[ -s "$RT_TARGET/chenge-preview-host" ] || { echo "  MISS chenge-runtime/chenge-preview-host" >&2; fail=$((fail + 1)); }
if [ -s "$RT_TARGET/chenge-runtime" ]; then
    echo "  带了进程版 chenge-runtime（截图预览用），目标机需要 libsdl2-2.0-0" >&2
fi
# 缺库只在打包机上看得到；装到目标机才发现就是"双击没反应"
if command -v ldd >/dev/null 2>&1; then
    for bin in "$PAYLOAD/HmiEditor.App" "$RT_TARGET/chenge-preview-host"; do
        [ -s "$bin" ] || continue
        missing=$(ldd "$bin" 2>/dev/null | awk '/not found/ { print $1 }' | tr '\n' ' ')
        if [ -n "$missing" ]; then
            echo "  注意：$(basename "$bin") 缺动态库：$missing（deb 的 Depends 已声明常用项）" >&2
        fi
    done
fi
[ "$fail" -eq 0 ] || { echo "不可出包：$fail 项有问题。" >&2; exit 1; }
printf '  ok    编辑器 %s + 运行时 %s\n' \
    "$(mb "$(find "$PAYLOAD" -maxdepth 1 -type f -printf '%s\n' | awk '{ s += $1 } END { print s + 0 }')")" \
    "$(mb "$(du -sb "$RT_TARGET" | cut -f1)")"

step "5/5 打包"
if [ "$MAKE_TAR" = 1 ]; then
    TGZ="$OUT/Chenge-HMI-Editor-${VERSION}-linux-x64.tar.gz"
    rm -f "$TGZ"
    # 绿色版顶层带版本目录，解压到哪都不散落一地
    tar -czf "$TGZ" -C "$OUT" --transform "s,^linux-x64,${STAGE_NAME}," linux-x64
    printf '  tar.gz %s（%s）\n' "$TGZ" "$(mb "$(size "$TGZ")")"
fi

if [ "$MAKE_DEB" = 1 ]; then
    if ! command -v dpkg-deb >/dev/null 2>&1; then
        echo "  跳过 .deb：没有 dpkg-deb（apt install dpkg）" >&2
    else
        DEB_ROOT="$OUT/.deb-root"
        APPDIR="$DEB_ROOT/opt/chenge-hmi-editor"
        rm -rf "$DEB_ROOT"
        mkdir -p "$APPDIR" "$DEB_ROOT/DEBIAN" "$DEB_ROOT/usr/bin" \
                 "$DEB_ROOT/usr/share/applications"
        cp -a "$PAYLOAD"/. "$APPDIR"/
        ln -s /opt/chenge-hmi-editor/chenge-hmi-editor "$DEB_ROOT/usr/bin/chenge-hmi-editor"
        cat > "$DEB_ROOT/usr/share/applications/chenge-hmi-editor.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=晨歌 HMI 编辑器
Comment=HMI 组态编辑器（内置板端预览运行时）
Exec=/opt/chenge-hmi-editor/chenge-hmi-editor %f
Icon=applications-engineering
Terminal=false
Categories=Development;
MimeType=application/x-hmi-project;
DESKTOP
        # Depends 是保守下限：libicu 的包名随发行版变，故列近几代择一；
        # libsdl2 只被可选的进程版用到，但缺它截图预览会静默降级，仍作硬依赖声明
        cat > "$DEB_ROOT/DEBIAN/control" <<CONTROL
Package: chenge-hmi-editor
Version: ${VERSION}
Section: devel
Priority: optional
Architecture: amd64
Maintainer: 晨歌 HMI <hmi@chenge.local>
Installed-Size: $(du -sk "$APPDIR" | cut -f1)
Depends: libc6 (>= 2.31), libstdc++6, libx11-6, libxcb1, libice6, libsm6, libfontconfig1, libfreetype6, libsdl2-2.0-0, libicu76 | libicu74 | libicu72 | libicu70
Recommends: fonts-noto-cjk
Description: 晨歌 HMI 组态编辑器
 图形化 HMI 组态编辑器（Avalonia + .NET 10 自包含，目标机不需装 .NET）。
 随包内置板端预览运行时，装完即可画布与实时预览，不需要再指定运行时目录。
CONTROL
        DEB="$OUT/chenge-hmi-editor_${VERSION}_amd64.deb"
        rm -f "$DEB"
        dpkg-deb --root-owner-group -Zxz -b "$DEB_ROOT" "$DEB" >/dev/null
        rm -rf "$DEB_ROOT"
        printf '  deb    %s（%s）\n' "$DEB" "$(mb "$(size "$DEB")")"
    fi
fi

echo
echo "完成：$OUT"

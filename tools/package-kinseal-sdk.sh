#!/usr/bin/env bash
# 把一次**已验证能编 .so** 的构建产物打成 kinseal SDK 包，供编辑器随安装包分发。
#
# 产物（默认落 <repo>/sdk/）：
#   kinseal-sdk-<version>.tar.gz            —— 归档根下只有 runtime/ 与 toolchain/
#   kinseal-sdk-<version>.manifest.json     —— 版本 / 大小 / SHA-256（编辑器按它校验）
#
# 本脚本**不猜任何路径**：--runtime 与 --host 必须显式给（或用同名环境变量）。
# 打包完成后会用**拷进去的那份工具链**真编一个 ARM .so 自检 —— 不可搬迁的工具链
# 在这里就该失败，而不是等用户装完才发现编不了。
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_DIR="${CHENGE_RT_DIR:-}"
HOST_DIR="${BUILDROOT_HOST_DIR:-}"
OUT_DIR="$REPO_ROOT/sdk"
VERSION="1.0.0"
SLIM=0

usage() {
    cat <<'EOF'
用法: package-kinseal-sdk.sh --runtime <chenge-rt> --host <buildroot/output/host> [选项]

必填（也可用环境变量 CHENGE_RT_DIR / BUILDROOT_HOST_DIR）:
  --runtime <dir>   设备侧运行时树（含 src/runtime/hmi_ui_api.h、third_party/lvgl-*）
  --host <dir>      buildroot 的 output/host（含 bin/lib/libexec/<target>）

选项:
  --out <dir>       产物目录（默认 <repo>/sdk）
  --version <ver>   版本号（默认 1.0.0）
  --slim            去掉 C 编译用不到的部件（host python2.7、cc1plus、lto1）
  -h, --help        本帮助

说明:
  - 归档布局固定为 runtime/ 与 toolchain/，编辑器按此安装；换布局要同步改安装器的判别文件。
  - 打包末尾会做自检：-print-sysroot 必须指向包内、并真编出一个 ARM ELF .so。
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --runtime) RUNTIME_DIR="${2:-}"; shift 2 ;;
        --host)    HOST_DIR="${2:-}";    shift 2 ;;
        --out)     OUT_DIR="${2:-}";     shift 2 ;;
        --version) VERSION="${2:-}";     shift 2 ;;
        --slim)    SLIM=1;               shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "未知参数: $1" >&2; usage >&2; exit 2 ;;
    esac
done

die() { echo "错误: $*" >&2; exit 1; }

[ -n "$RUNTIME_DIR" ] || die "缺少 --runtime（chenge-rt 目录）"
[ -n "$HOST_DIR" ]    || die "缺少 --host（buildroot output/host 目录）"
[ -f "$RUNTIME_DIR/src/runtime/hmi_ui_api.h" ] || die "不是运行时树: $RUNTIME_DIR（缺 src/runtime/hmi_ui_api.h）"
[ -x "$HOST_DIR/bin/arm-buildroot-linux-gnueabi-gcc" ] || die "不是 buildroot host: $HOST_DIR（缺交叉 gcc）"

SYSROOT_DIR="$(ls -d "$HOST_DIR"/*-buildroot-linux-*/sysroot 2>/dev/null | head -1 || true)"
[ -n "$SYSROOT_DIR" ] || die "$HOST_DIR 下找不到 *-buildroot-linux-*/sysroot"
TARGET_DIR="$(dirname "$SYSROOT_DIR")"   # <host>/arm-buildroot-linux-gnueabi
TARGET_NAME="$(basename "$TARGET_DIR")"

LVGL_DIR="$(ls -d "$RUNTIME_DIR"/third_party/lvgl-* 2>/dev/null | head -1 || true)"
[ -n "$LVGL_DIR" ] || die "$RUNTIME_DIR/third_party 下找不到 lvgl-*"

WORK="$OUT_DIR/.staging-$$"
trap 'rm -rf "$WORK"' EXIT
rm -rf "$WORK"
mkdir -p "$WORK/runtime/third_party" "$WORK/toolchain"

echo "[1/5] 运行时（不含 third_party / objs / 生成物）"
tar -C "$RUNTIME_DIR" -cf - \
    --exclude=./third_party --exclude=./objs --exclude=./.git \
    --exclude='*.o' --exclude='*.so' --exclude=./generated/screens.c \
    . | tar -C "$WORK/runtime" -xf -

echo "[2/5] LVGL 源码（去掉 tests/examples/demos/docs 与编译产物）"
mkdir -p "$WORK/runtime/third_party/$(basename "$LVGL_DIR")"
tar -C "$LVGL_DIR" -cf - \
    --exclude=./tests --exclude=./examples --exclude=./demos --exclude=./docs \
    --exclude='*.o' --exclude='*.a' --exclude=./build \
    . | tar -C "$WORK/runtime/third_party/$(basename "$LVGL_DIR")" -xf -

echo "[3/5] 工具链（bin / lib / libexec / $TARGET_NAME）"
cp -a "$HOST_DIR/bin" "$WORK/toolchain/"
cp -a "$HOST_DIR/lib" "$WORK/toolchain/"
cp -a "$HOST_DIR/libexec" "$WORK/toolchain/"
cp -a "$TARGET_DIR" "$WORK/toolchain/"

if [ "$SLIM" -eq 1 ]; then
    echo "      --slim: 去掉 host python2.7 与 C 用不到的前端"
    rm -rf "$WORK/toolchain/lib/python2.7" "$WORK/toolchain/lib"/libpython2.7*
    rm -f  "$WORK/toolchain/libexec/gcc/$TARGET_NAME"/*/cc1plus
    rm -f  "$WORK/toolchain/libexec/gcc/$TARGET_NAME"/*/lto1
fi

cat > "$WORK/sdk.json" <<EOF
{
  "version": "$VERSION",
  "builtAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "slim": $SLIM,
  "runtimeSource": "$RUNTIME_DIR",
  "toolchainSource": "$HOST_DIR",
  "lvgl": "$(basename "$LVGL_DIR")"
}
EOF

echo "[4/5] 自检（用包内工具链编一个 ARM .so）"
TC_GCC="$WORK/toolchain/bin/arm-buildroot-linux-gnueabi-gcc"
[ -x "$TC_GCC" ] || die "包内缺少可执行 gcc（权限丢了？）"
SYSROOT_SEEN="$("$TC_GCC" -print-sysroot)"
case "$SYSROOT_SEEN" in
    "$WORK/toolchain/"*) : ;;
    *) die "工具链不可搬迁：-print-sysroot 指向包外（$SYSROOT_SEEN）" ;;
esac
CHECK_DIR="$WORK/.selfcheck"
mkdir -p "$CHECK_DIR"
printf 'int kinseal_sdk_selftest(void){return 1;}\n' > "$CHECK_DIR/m.c"
"$TC_GCC" -fPIC -c "$CHECK_DIR/m.c" -o "$CHECK_DIR/m.o"
"$TC_GCC" -shared -nostartfiles -o "$CHECK_DIR/m.so" "$CHECK_DIR/m.o"
if ! file "$CHECK_DIR/m.so" | grep -q "ARM, EABI"; then
    die "自检产物不是 ARM EABI 共享库：$(file "$CHECK_DIR/m.so")"
fi
rm -rf "$CHECK_DIR"

echo "[5/5] 归档"
mkdir -p "$OUT_DIR"
NAME="kinseal-sdk-$VERSION"
ARCHIVE="$OUT_DIR/$NAME.tar.gz"
rm -f "$ARCHIVE"
tar -C "$WORK" -czf "$ARCHIVE" runtime toolchain sdk.json

SHA="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
SIZE="$(stat -c %s "$ARCHIVE")"
MANIFEST="$OUT_DIR/$NAME.manifest.json"
cat > "$MANIFEST" <<EOF
{
  "schemaVersion": 1,
  "version": "$VERSION",
  "url": "",
  "sha256": "$SHA",
  "sizeBytes": $SIZE
}
EOF

echo
echo "完成:"
echo "  归档    $ARCHIVE  ($(du -h "$ARCHIVE" | cut -f1))"
echo "  清单    $MANIFEST"
echo "  sha256  $SHA"
echo "  解压后  $(du -sh --exclude='.selfcheck' "$WORK" | cut -f1)"
cat <<'EOF'

下一步:
  1) 编辑器随包分发：把上面两个文件放进编辑器输出目录的 sdk/（构建时由 csproj 自动带上）；
  2) 首次使用时编辑器会校验清单并把归档装到 <用户数据目录>/sdk/kinseal；
  3) 内网/离线分发：直接发送这两个文件，用户在「设置→真机部署」里选本地安装包亦可。
EOF

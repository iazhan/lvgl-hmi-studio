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
KIND="gcc"                 # gcc = buildroot 交叉 gcc（仅 Linux）；clang = LLVM（跨平台，含 Windows）
PLATFORM=""                # 归档名后缀，默认按本机推导（linux-x64 / win-x64）
SYSROOT_DIR=""             # clang：ARM sysroot
LIBGCC_FILE=""             # clang：libgcc.a
CC_NAME=""                 # clang：可执行名（默认 clang）
LD_NAME=""                 # clang：链接器名（默认 ld.lld）
EXTRA_LIBS=()              # clang：额外 .so/.dll，拷进 toolchain/lib
HOST_TOOLCHAIN=""          # clang：clang 发行版根（含 bin/）

usage() {
    cat <<'EOF'
用法: package-kinseal-sdk.sh --runtime <chenge-rt> --host <buildroot/output/host> [选项]

必填（也可用环境变量 CHENGE_RT_DIR / BUILDROOT_HOST_DIR）:
  --runtime <dir>   设备侧运行时树（含 src/runtime/hmi_ui_api.h、third_party/lvgl-*）
  --host <dir>      buildroot 的 output/host（含 bin/lib/libexec/<target>）

选项:
  --out <dir>       产物目录（默认 <repo>/sdk）
  --version <ver>   版本号（默认 1.0.0）
  --slim            去掉 C 编译用不到的部件（host python2.7、cc1plus、lto1；仅 --kind gcc）
  --kind gcc|clang  工具链种类。gcc = buildroot 交叉 gcc（只能 Linux 上跑）；
                    clang = LLVM（Windows/Linux 通用，配套 --sysroot/--libgcc）
  --platform <tag>  归档名平台后缀（默认按本机推导：linux-x64 / win-x64）
  --sysroot <dir>   clang：ARM sysroot（默认 <host>/<target>/sysroot）
  --libgcc <file>   clang：libgcc.a（默认 <host>/lib/gcc/<target>/*/libgcc.a）
  --cc-name <name>  clang 可执行名（默认 clang）
  --ld-name <name>  clang 链接器名（默认 ld.lld；Ubuntu 包常是 ld.lld-19）
  --extra-lib <f>   额外 .so/.dll 拷进 toolchain/lib（可重复；发行版不自带时用）
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
        --kind)         KIND="${2:-}";         shift 2 ;;
        --host-toolchain) HOST_TOOLCHAIN="${2:-}"; shift 2 ;;
        --platform)     PLATFORM="${2:-}";     shift 2 ;;
        --sysroot)      SYSROOT_DIR="${2:-}";  shift 2 ;;
        --libgcc)       LIBGCC_FILE="${2:-}";  shift 2 ;;
        --cc-name)      CC_NAME="${2:-}";      shift 2 ;;
        --ld-name)      LD_NAME="${2:-}";      shift 2 ;;
        --extra-lib)    EXTRA_LIBS+=("${2:-}"); shift 2 ;;
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

# 平台标签：工具链与宿主绑定（Linux 版是 ELF，Windows 版是 .exe），归档名必须带平台，
# 安装器才好在"只有别平台 SDK"时给出明确提示，而不是笼统的"未找到 SDK"。
[ -n "$PLATFORM" ] || PLATFORM="$(uname -s 2>/dev/null | tr 'A-Z' 'a-z' \
    | sed -e 's/^mingw.*/win/' -e 's/^msys.*/win/' -e 's/^cygwin.*/win/' -e 's/^darwin.*/mac/')-x64"

# clang 模式的默认值：ARM sysroot 与 libgcc 都在 buildroot host 里（与 gcc 版同一份头文件）
TARGET_TRIPLE="armv5te-linux-gnueabi"
if [ "$KIND" = "clang" ]; then
    [ -n "$SYSROOT_DIR" ] || SYSROOT_DIR="$HOST_DIR/$TARGET_NAME/sysroot"
    [ -n "$LIBGCC_FILE" ] || LIBGCC_FILE="$(ls -1 "$HOST_DIR"/lib/gcc/"$TARGET_NAME"/*/libgcc.a 2>/dev/null | head -1)"
fi

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

echo "[3/5] 工具链（kind=$KIND）"
if [ "$KIND" = "gcc" ]; then
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

    cat > "$WORK/toolchain/toolchain.json" <<EOF
{ "kind": "gcc", "cc": "arm-buildroot-linux-gnueabi-gcc" }
EOF
else
    # clang：整棵树拷进来（官方发行版自带 bin/ + lib/），再补 ARM sysroot 与 libgcc。
    # 编译只需要头文件（.so 不链接任何库），链接只需要 libgcc（软浮点 __aeabi_* 例程）。
    [ -n "$HOST_TOOLCHAIN" ] || die "--kind clang 需要 --host-toolchain <clang 发行版根，含 bin/>"
    [ -d "$HOST_TOOLCHAIN/bin" ] || die "$HOST_TOOLCHAIN 下没有 bin/（不是 clang 发行版根？）"
    mkdir -p "$WORK/toolchain"
    (cd "$HOST_TOOLCHAIN" && tar -cf - bin lib share 2>/dev/null) | tar -C "$WORK/toolchain" -xf - 2>/dev/null \
        || { mkdir -p "$WORK/toolchain"; cp -a "$HOST_TOOLCHAIN/bin" "$HOST_TOOLCHAIN/lib" "$WORK/toolchain/" 2>/dev/null; }

    # sysroot / libgcc 的默认值在上面已推导（都取 buildroot host），此处只做存在性校验
    [ -d "$SYSROOT_DIR" ] || die "clang 模式需要 --sysroot <ARM sysroot>（buildroot 的 output/host/<target>/sysroot）"
    mkdir -p "$WORK/toolchain"
    cp -a "$SYSROOT_DIR" "$WORK/toolchain/sysroot"

    [ -f "$LIBGCC_FILE" ] || die "clang 模式需要 --libgcc <libgcc.a>（不链它，__aeabi_* 会留在未定义符号里）"
    mkdir -p "$WORK/toolchain/lib"
    cp -a "$LIBGCC_FILE" "$WORK/toolchain/lib/"
    for extra in ${EXTRA_LIBS+"${EXTRA_LIBS[@]}"}; do
        [ -f "$extra" ] || die "--extra-lib 指向的文件不存在：$extra"
        cp -a "$extra" "$WORK/toolchain/lib/"
    done

    cat > "$WORK/toolchain/toolchain.json" <<EOF
{
  "kind": "clang",
  "cc": "${CC_NAME:-clang}",
  "target": "$TARGET_TRIPLE",
  "floatAbi": "soft",
  "sysroot": "sysroot",
  "ld": "${LD_NAME:-ld.lld}",
  "libDir": "lib",
  "libName": "gcc",
  "noStdlib": true
}
EOF
fi
[ -s "$WORK/toolchain/toolchain.json" ] || die "toolchain.json 没写出来"

cat > "$WORK/sdk.json" <<EOF
{
  "version": "$VERSION",
  "builtAtUtc": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "slim": $SLIM,
  "runtimeSource": "$RUNTIME_DIR",
  "toolchainSource": "$HOST_DIR${HOST_TOOLCHAIN:+ (clang: $HOST_TOOLCHAIN)}",
  "lvgl": "$(basename "$LVGL_DIR")"
}
EOF

echo "[4/5] 自检（用包内工具链编一个 ARM .so）"
CHECK_DIR="$WORK/.selfcheck"
mkdir -p "$CHECK_DIR"
printf 'int kinseal_sdk_selftest(void){return 1;}\n' > "$CHECK_DIR/m.c"
if [ "$KIND" = "gcc" ]; then
    TC_CC="$WORK/toolchain/bin/arm-buildroot-linux-gnueabi-gcc"
    [ -x "$TC_CC" ] || die "包内缺少可执行 gcc（权限丢了？）"
    SYSROOT_SEEN="$("$TC_CC" -print-sysroot)"
    case "$SYSROOT_SEEN" in
        "$WORK/toolchain/"*) : ;;
        *) die "工具链不可搬迁：-print-sysroot 指向包外（$SYSROOT_SEEN）" ;;
    esac
    "$TC_CC" -fPIC -c "$CHECK_DIR/m.c" -o "$CHECK_DIR/m.o"
    "$TC_CC" -shared -nostartfiles -o "$CHECK_DIR/m.so" "$CHECK_DIR/m.o"
else
    TC_CC="$WORK/toolchain/bin/${CC_NAME:-clang}"
    [ -x "$TC_CC" ] || die "包内缺少可执行 clang：$TC_CC"
    TC_LD="$WORK/toolchain/bin/${LD_NAME:-ld.lld}"
    [ -x "$TC_LD" ] || die "包内缺少链接器：$TC_LD（clang 发行版应自带；Linux 包常叫 ld.lld-<版本>，用 --ld-name 指定）"
    "$TC_CC" --target="$TARGET_TRIPLE" -mfloat-abi=soft --sysroot="$WORK/toolchain/sysroot" \
        -fPIC -c "$CHECK_DIR/m.c" -o "$CHECK_DIR/m.o"
    "$TC_CC" --target="$TARGET_TRIPLE" -mfloat-abi=soft --sysroot="$WORK/toolchain/sysroot" \
        -shared -nostdlib -nostartfiles -fuse-ld="$TC_LD" \
        -L"$WORK/toolchain/lib" -lgcc -o "$CHECK_DIR/m.so" "$CHECK_DIR/m.o"
fi
if ! file "$CHECK_DIR/m.so" | grep -q "ARM, EABI"; then
    die "自检产物不是 ARM EABI 共享库：$(file "$CHECK_DIR/m.so")"
fi
rm -rf "$CHECK_DIR"

echo "[5/5] 归档"
mkdir -p "$OUT_DIR"
NAME="kinseal-sdk-$VERSION-$PLATFORM"
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
     归档名带平台后缀（-linux-x64 / -win-x64），安装器只认本平台那份；
  2) 首次使用时编辑器会校验清单并把归档装到 <用户数据目录>/sdk/kinseal；
  3) 内网/离线分发：直接发送这两个文件，用户在「设置→真机部署」里选本地安装包亦可。
EOF

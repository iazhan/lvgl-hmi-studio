# third_party

## lua-5.4.7

- 来源：<https://www.lua.org/ftp/lua-5.4.7.tar.gz>
- sha256：`9fbf5e28ef86c69858f6d3d34eccc32e911c1a28b4120ff3e84aaa70cfbf1e30`
- 体积：源码 1.5 MB（`src/` 约 1.1 MB），编译进固件的核心约 300 KB
- 许可：MIT（与 LVGL 不同，可静态链、无「可重新链接」义务）

### 本地改动（仅一处）

`src/lctype.h` 的 `lislalpha` / `lislalnum`：把 `0x80..0xFF` 的字节按字母处理，
让 UTF-8 标识符（中文变量名、中文页面名）能直接出现在脚本里（`37` §7.1）。

```c
#define lislalpha(c)  (testprop(c, MASK(ALPHABIT)) || \
                       ((unsigned)(c) >= 0x80u && (unsigned)(c) <= 0xFFu))
#define lislalnum(c)  (testprop(c, (MASK(ALPHABIT) | MASK(DIGITBIT))) || \
                       ((unsigned)(c) >= 0x80u && (unsigned)(c) <= 0xFFu))
```

**必须按 `0x80..0xFF` 区间判断，不能只测最高位**：Lua 的 `ls->current` 在文件末尾是
`EOF`（`-1`），`(unsigned)(-1) & 0x80` 为真，EOF 会被当成字母吞进 token——实测现象是
`return 1+1` 报 `malformed number near '1`、`while true do end` 报 `syntax error`。

### 编哪些、不编哪些

`Makefile` 里排除了 `lua.c`（解释器 main）、`luac.c`（编译器 main）、`onelua.c`、
`linit.c`（会 `open` 全部库）、`liolib.c`、`loadlib.c`、`loslib.c`。

沙箱库由 `src/script/lua_rt.c` 自己开：`base` / `coroutine` / `table` / `string` /
`math` / `utf8`，并删掉 `load`、`loadfile`、`dofile`、`collectgarbage`、`require`、`print`；
`os` 只保留 `time` / `clock` / `difftime`（`date` 待实现）。

### 升级步骤

1. 下载新版本 tarball，核对 sha256，解压覆盖本目录
2. 重新打 `lctype.h` 的那一处补丁（见上）
3. 跑 `make HOST=1`，用 `HMI_LUA_RUN='...'` 自检：算术、UTF-8 标识符、死循环限额

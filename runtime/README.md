# runtime/ —— 出包用的运行时兜底副本

编辑器（设计画布 / 实时预览 / 截图预览）跑的不是自己造的一套渲染，而是板端同源的
`chenge-preview-host`。这个目录把那份运行时**取一份放在本仓库里**，这样打包不依赖
主仓库是否在本机、路径是否还叫那个名字。

```
runtime/
├── VERSION          取用时的运行时版本（与主仓库 chenge-runtime/VERSION 同值）
├── SOURCE.txt       来源目录、来源提交、取用时间，以及更新它要跑的命令
├── linux-x64/       chenge-preview-host、chenge-runtime、libchenge_runtime.so、sample/
├── win-x64/         chenge-preview-host.exe、sample/
└── src/             运行时源码副本（契约头与实现留档）
```

## 谁在用

`tools/package.sh` 与 `tools/publish-{linux,win}.sh`：不传 `--runtime-*` 时默认读
`runtime/<平台>/`。优先级是 `--build-runtime`（现编）> `--runtime-*`（指定目录）> 本目录。

## 更新它

**不要手改里面的二进制**。要换版本，先在主仓库编出新产物，再整目录同步：

```bash
make -C <主仓库>/chenge-hmi/chenge-runtime PREVIEW=1 HOST=1    # 画布宿主
make -C <主仓库>/chenge-hmi/chenge-runtime HOST=1              # 截图预览的进程版
make -C <主仓库>/chenge-hmi/chenge-runtime HOST=1 WINDOWS=1    # Windows 宿主（mingw-w64）
bash tools/sync-runtime.sh --from <主仓库>/chenge-hmi/chenge-runtime
```

`sync-runtime.sh` 会顺带重写 `VERSION` 和 `SOURCE.txt`，并把旧平台的产物整目录替换掉 ——
不会出现"上一版的 .so 混在新版的宿主旁边"。

## 不变式

- `src/` 是**只读留档**：它依赖主仓库的 `chenge-common/common.mk` 与 LVGL 源码树，
  **不能在本仓库单独编译**。要编运行时去主仓库，编完用上面的命令取回来。
- 两个平台的宿主必须同一版本：编辑器与宿主之间的协议随版本变，混用会出现
  "画布能开、点一下就没反应"这类没有报错的症状。判据是 `VERSION` 与 `SOURCE.txt`。

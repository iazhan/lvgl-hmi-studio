# docs/ 地图

本目录只留**在用**的三份文档。判据（测试、脚本）要求的文档必须留在这里，其余专题记录不放在工作树里。

| 文件 | 谁在用 |
|---|---|
| `user-guide.md` | 现场工程师手册；`UserGuideDocTests` 扫它（路径/链接/快捷键/脚本可执行位） |
| `troubleshooting.md` | 故障排查手册；同上 |
| `hardware/f1c100s-kinseal/01-board-facts.md` | 板级事实表；`ResourceBudgetTests` 读它的 `Resources.RamSizeBytes` 行 |

## 代码注释里的 `docs/...md` 找不到文件时

专题记录（设计决策、踩坑取证、阶段台账）已移出工作树，仍在 git 历史里，按文件名就能取回：

```bash
git log --oneline --diff-filter=D -- 'docs/<文件名>.md'   # 找删除它的提交
git show <提交>^:docs/<文件名>.md > /tmp/notes.md          # 取出删除前的内容
git log --oneline --all -- docs/                          # 列所有历史文档提交
```

代码里写着的那些路径是**指出处**，不是构建输入 —— 文件不在工作树不影响编译、测试与出包；
真要读那份记录时按上面三条命令取。

## 加新文档的规矩

只有当**判据要读它**（测试里 `File.ReadAllText` / 正则断言，或 `UserGuideDocTests` 要扫）时才放进这个目录；
纯说明性的方案、复盘、阶段记录放进 commit message 或 git 历史，别在这里堆。

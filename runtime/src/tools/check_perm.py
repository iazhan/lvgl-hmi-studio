#!/usr/bin/env python3
"""check_perm.py — 命名权限位词表的一致性检查

两处必须一致，否则编辑器给用户勾的位与固件判的位会错位：

  spec/perms.json          词表真源（编辑器与文档读它）
  src/core/perm.h          运行时的位定义 + HMI_PERM_LIST 名字表

另外核对 docs/chenge-rt/37 的权限表仍列出全部 10 个名字 —— 文档漏一个新位，
现场就不知道 users.json 里那个名字从哪来。

用法：python3 tools/check_perm.py
退出码 0 = 一致；1 = 有差异（golden.sh 会先拦这一道）。
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
RT = os.path.dirname(HERE)                      # chenge-hmi/chenge-runtime
ROOT = os.path.abspath(os.path.join(RT, "..", ".."))

SPEC = os.path.join(ROOT, "spec", "perms.json")
PERM_H = os.path.join(RT, "src", "core", "perm.h")
DOC = os.path.join(ROOT, "docs", "chenge-rt", "37-组态工程文件规范.md")

problems = []


def read(path):
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def main():
    spec = json.loads(read(SPEC))
    h = read(PERM_H)

    want = {b["name"]: b["bit"] for b in spec["bits"]}
    if len(want) != len(spec["bits"]):
        problems.append("spec/perms.json 里有重名权限")

    # perm.h 的位定义：PERM_XXX = 1u << N
    consts = {"PERM_" + m.group(1): int(m.group(2))
              for m in re.finditer(r"PERM_([A-Z_]+)\s*=\s*1u\s*<<\s*(\d+)", h)}

    # HMI_PERM_LIST(X) 里 X(name, PERM_NAME) 的顺序即词表
    body = h[h.find("HMI_PERM_LIST(X)"):]
    body = body[:body.find("\n\n")] if "\n\n" in body else body
    listed = {}
    for m in re.finditer(r"X\(\s*([a-z0-9_]+)\s*,\s*PERM_([A-Z_]+)\s*\)", body):
        name, macro = m.group(1), "PERM_" + m.group(2)
        if macro not in consts:
            problems.append("HMI_PERM_LIST 引用了 perm.h 里没有的 %s" % macro)
            continue
        listed[name] = consts[macro]

    if not listed:
        problems.append("perm.h 里没解析到 HMI_PERM_LIST（格式变了？）")

    for name in sorted(set(want) - set(listed)):
        problems.append("权限 %s 在 spec/perms.json 有、perm.h 没有" % name)
    for name in sorted(set(listed) - set(want)):
        problems.append("权限 %s 在 perm.h 有、spec/perms.json 没有" % name)
    for name, bit in sorted(listed.items()):
        if name in want and want[name] != bit:
            problems.append("权限 %s 的位号不一致：perm.h=%d，spec/perms.json=%d"
                            % (name, bit, want[name]))

    if len(listed) != 10:
        problems.append("命名权限位应为 10 个，perm.h 里有 %d 个（PERM_ALL 的掩码要跟着改）" % len(listed))
    m_all = re.search(r"PERM_ALL\s*=\s*\(1u\s*<<\s*(\d+)\)\s*-\s*1", h)
    allbits = int(m_all.group(1)) if m_all else None
    if allbits != len(listed):
        problems.append("PERM_ALL 应覆盖 (1<<%d)-1，perm.h 写的是 1<<%s"
                        % (len(listed), allbits))

    doc = read(DOC)
    for name in sorted(want):
        if "`%s`" % name not in doc:
            problems.append("docs/chenge-rt/37 的权限表里没有 `%s`" % name)

    if problems:
        print("!! 命名权限位不一致（%d 处）：" % len(problems))
        for p in problems:
            print("   - %s" % p)
        return 1

    print("命名权限位一致：%d 个（spec/perms.json ↔ perm.h ↔ 37）" % len(listed))
    return 0


if __name__ == "__main__":
    sys.exit(main())

# 版本号规则：major 固定 2，只动后两位（2.x.y）。把批次 4/5 误升到 3.x 的订正回来。
p = 'src/HmiEditor.Core/VersionInfo.cs'
s = open(p, encoding='utf-8').read()
s = s.replace('public const string Version = "3.1.0";', 'public const string Version = "2.11.0";')
s = s.replace('    /// <summary>主版本号</summary>',
              '    /// <summary>\n'
              '    /// 版本号：**major 固定 2**，只递增后两位（2.x.y）。\n'
              '    /// 新增控件/属性属于 minor（2.x.0），修缺陷属于 patch（2.x.y）。\n'
              '    /// </summary>')
open(p, 'w', encoding='utf-8').write(s)

p = 'MAPPING-REPORT.md'
s = open(p, encoding='utf-8').read()
s = s.replace('## 8 批次 3/4/5 之后的覆盖面（v3.1.0）', '## 8 批次 3/4/5 之后的覆盖面（v2.11.0）')
open(p, 'w', encoding='utf-8').write(s)
print(open('src/HmiEditor.Core/VersionInfo.cs', encoding='utf-8').read()[:600])

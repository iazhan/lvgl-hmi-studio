using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 导出产物**逐字节**golden：把验收工程（<see cref="ExportFixture"/>）导一遍，与
/// <c>Baselines/panel-redo/</c> 下登记的每个文件逐字节比较。
///
/// <para>为什么不用 JSON 语义相等：语义比较会漏掉"键顺序变了、缩进变了、多一个空数组"
/// 这类变化，而这些正是（a）老工程被人无意改坏、（b）面板重做顺手改了缺省路径的症状。
/// 逐字节比较才挡得住。</para>
///
/// <para>基线怎么更新：确认 diff 是**有意**的（新增属性恰好只在被设置的控件上出现、
/// 没有把缺省路径改写），删掉 <c>Baselines/panel-redo</c> 后带
/// <c>GOLDEN_ACCEPT=1</c> 跑一次，再把新基线随改动一起提交。</para>
/// </summary>
public class PanelRedoByteGoldenTests
{
    private static string BaselineDir => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Baselines", "panel-redo");

    private static string RepoBaselineDir => Path.GetFullPath(BaselineDir);

    [Fact]
    public void AcceptanceProject_ExportsByteIdenticallyToTheRecordedBaseline()
    {
        var output = TestTempDir.NewDir("panel-redo-golden");
        try
        {
            var result = ExportFixture.Export(ExportFixture.Build(), output);
            Assert.True(result.Success, string.Join("\n", result.Issues.Items));

            var produced = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(output, p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            Assert.NotEmpty(produced);

            if (Environment.GetEnvironmentVariable("GOLDEN_ACCEPT") == "1")
            {
                if (Directory.Exists(RepoBaselineDir)) Directory.Delete(RepoBaselineDir, true);
                foreach (var rel in produced)
                {
                    var dst = Path.Combine(RepoBaselineDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(Path.Combine(output, rel), dst, true);
                }
                return;
            }

            Assert.True(Directory.Exists(RepoBaselineDir),
                $"缺少基线目录 {RepoBaselineDir}（首次生成：GOLDEN_ACCEPT=1 dotnet test --filter PanelRedoByteGoldenTests）");

            var baseline = Directory.EnumerateFiles(RepoBaselineDir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(RepoBaselineDir, p))
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            var problems = new List<string>();
            foreach (var rel in baseline.Except(produced))
                problems.Add($"{rel}: 基线有、产物没有");
            foreach (var rel in produced.Except(baseline))
                problems.Add($"{rel}: 产物多出这个文件（基线里没有）");

            foreach (var rel in baseline.Intersect(produced))
            {
                var a = File.ReadAllBytes(Path.Combine(RepoBaselineDir, rel));
                var b = File.ReadAllBytes(Path.Combine(output, rel));
                if (a.Length != b.Length || !a.AsSpan().SequenceEqual(b))
                    problems.Add($"{rel}: 字节不一致（基线 {a.Length}B / 产物 {b.Length}B）");
            }

            Assert.True(problems.Count == 0,
                "导出产物与逐字节基线不一致：" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Take(20)));
        }
        finally
        {
            TestTempDir.DeleteDir(output);
        }
    }
}

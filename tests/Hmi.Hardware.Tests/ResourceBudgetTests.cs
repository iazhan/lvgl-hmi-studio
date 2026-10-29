using System.IO;
using System.Text.RegularExpressions;
using Hmi.Hardware.Builtin;
using Hmi.Hardware.Contracts;
using Xunit;

namespace Hmi.Hardware.Tests;

/// <summary>
/// **资源预算判据**（登记表 §4 第 29 项"资源预算校准"）。
///
/// <para>这项登记的原文是"<c>ResourceConstraints</c> 只有声明值，无『实测 vs 预算』判据"。
/// 本类钉两件事：</para>
/// <list type="number">
///   <item><b>声明值必须与它的硬件证据对得上</b> —— kinseal 的内存预算取自板级事实表
///         （32 MiB 内置 DDR，无 swap）；这条用
///         <c>docs/hardware/f1c100s-kinseal/01-board-facts.md</c> 的表格行锁住（错了立刻红）；</item>
///   <item><b>判读规则本身</b> —— <see cref="ResourceBudget"/> 的阈值/未知预算语义
///         （仪表盘与实测工具共用同一处）。</item>
/// </list>
/// <para>真实体积读数（整包 vs 预算、UDISK 剩余 vs UI 产物）由
/// <c>tools/BudgetCheck</c> 在存在 SDK 产物的机器上跑出，见
/// <c>docs/2026-09-18-resource-budget-measurement.md</c>。</para>
/// </summary>
public class ResourceBudgetTests
{
    private static string RepoFile(params string[] parts) =>
        Path.Combine(new[] { SourceScanner.RepoRoot }.Concat(parts).ToArray());

    [Fact]
    public void KinsealDeclaredRam_MatchesTheBoardFactsTable()
    {
        var declared = new AllwinnerF1C100STarget().Resources.RamSizeBytes;

        // 板级事实表的固定形状：| `Resources.RamSizeBytes` | 32 MiB | ...
        var doc = File.ReadAllText(RepoFile("docs", "hardware", "f1c100s-kinseal", "01-board-facts.md"));
        var match = Regex.Match(doc, @"`Resources\.RamSizeBytes`[^|]*\|\s*(\d+)\s*MiB",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success,
            "01-board-facts.md 里找不到 '| `Resources.RamSizeBytes` | <N> MiB |' 这条声明");
        Assert.Equal(declared / 1024 / 1024, long.Parse(match.Groups[1].Value));

        // 32 MiB 这个具体值也钉一下：文档与常量同时改错时仍能红
        Assert.Equal(32L * 1024 * 1024, declared);
    }

    [Fact]
    public void UndeclaredTargetBudget_IsUnknown_NotPassing()
    {
        var reading = ResourceBudget.Evaluate("flash", usedBytes: 4096, budgetBytes: 0);

        // 缺依据就不下结论：既不是"通过"也不是"超预算"
        Assert.Equal(BudgetVerdict.Unknown, reading.Verdict);
        Assert.Equal(0, reading.Percent);
        Assert.Equal(0, reading.FreeBytes);
    }

    [Theory]
    [InlineData(0, BudgetVerdict.Ok)]
    [InlineData(699, BudgetVerdict.Ok)]
    [InlineData(700, BudgetVerdict.Elevated)]      // 与仪表盘黄色阈值同源
    [InlineData(899, BudgetVerdict.Elevated)]
    [InlineData(900, BudgetVerdict.NearLimit)]     // 与仪表盘红色阈值同源
    [InlineData(1000, BudgetVerdict.NearLimit)]    // 恰好用满：装得下
    [InlineData(1001, BudgetVerdict.Over)]         // 超出一字节就是装不下
    public void Evaluate_UsesTheSharedThresholds(long used, BudgetVerdict expected)
    {
        var reading = ResourceBudget.Evaluate("axis", used, budgetBytes: 1000);

        Assert.Equal(expected, reading.Verdict);
        Assert.Equal(used / 1000.0 * 100.0, reading.Percent, precision: 6);
    }

    [Fact]
    public void FreeBytes_IsBudgetMinusUsed_AndNeverNegative()
    {
        Assert.Equal(400, ResourceBudget.Evaluate("a", 600, 1000).FreeBytes);
        Assert.Equal(0, ResourceBudget.Evaluate("a", 1500, 1000).FreeBytes);
    }
}

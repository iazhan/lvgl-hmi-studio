using System.Linq;
using HmiEditor.Core.Models.Scripting;
using HmiEditor.Core.Services;
using Xunit;

namespace HmiEditor.Core.Tests;

/// <summary>
/// **运行前检查**的判据（设备侧运行时符号）。
/// 见 <c>docs/2026-09-20-lua-dialect-alignment.md</c> §8「三条路径」第三行。
///
/// <para><b>它治的是什么</b>：<see cref="LuaRejectStage.Runtime"/> 那一类假阴性 ——
/// 脚本语法完全合法、设备上却因为没有那个库而炸。<c>bit32.band(1, 3)</c> 是一个**合法 Lua 表达式**，
/// 缺的是运行时的库，所以编辑器的真解析**在原理上看不见**它 ——
/// 换解析器、调基准、对齐版本，全都救不了。这就是本类型存在的全部理由。</para>
///
/// <para><b>本组最要紧的一条</b>是 <see cref="EngineValidate_RuntimeSymbol_IsWarningNotError"/>：
/// 运行前检查**不许**把脚本判红 —— 它报的是"板上那个版本没有这个库"（语法合法、运行期才炸），
/// 不是"代码写错了"。判红会挡住保存，而用户可能正想先写着。</para>
/// </summary>
public class LuaRuntimeCheckTests
{
    /// <summary>把整张符号表各写成一行，行号即序号 —— 一次同时测「认不认得出来」与「行号对不对」。</summary>
    private static string AllSymbolsAsLines() =>
        string.Join("\n", LuaDialect.RuntimeSymbols.Select(s => $"local a = {s.Symbol}"));

    // ── 元判据：表本身要能判 ──

    /// <summary>
    /// 在基准版本上**确实会触发**的条目不能太少 —— 否则这张表退化成"全可用"，扫了等于没扫。
    ///
    /// <para>表里同时留着几条在 5.4 上可用的名字（table.pack / table.unpack / math.type / utf8）：
    /// 它们是"与 5.1 相比才有"的历史依据，切换目标版本时还要用。</para>
    /// </summary>
    [Fact]
    public void SymbolsTriggeringOnBaseline_AreNotATokenFew()
    {
        var triggering = LuaDialect.RuntimeSymbols
            .Where(s => s.RemovedIn is { } r && r <= LuaDialect.ParserBaseline
                        || s.Minimum > LuaDialect.ParserBaseline)
            .ToList();

        Assert.True(triggering.Count >= 8,
            $"基准 {LuaDialect.ParserBaselineLabel} 上只有 {triggering.Count} 条会触发，扫描器形同虚设");
    }

    /// <summary>
    /// <see cref="LuaRuntimeSymbol.Symbol"/> 只允许「标识符」或「标识符.标识符」——
    /// 它是**要被扫描的字面符号**，不是正则。写成别的（<c>bit32.*</c> 之类）
    /// 相当于把自己的正则注进扫描器：后果是满屏误报，而且很难看出原因。
    /// </summary>
    [Fact]
    public void SymbolShape_IsIdentifierOrDottedIdentifier()
    {
        foreach (var s in LuaDialect.RuntimeSymbols)
        {
            Assert.Matches(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$", s.Symbol);
        }
    }

    // ── 正控：干净代码零提示（"目标未定"是最容易恒红的场景）──

    /// <summary>
    /// 只用基准内就有东西的代码 ⇒ **零命中**。
    ///
    /// <para>注意其中两条带点的：<c>table.insert</c> 与 <c>math.floor</c> ——
    /// <c>table</c> / <c>math</c> 这两个**库本身** 5.1 就有，敏感的是后来新增的**字段**。
    /// 没有这条，"见到 <c>xxx.yyy</c> 就报"这种坏实现也能骗过下面所有"会报"的断言。</para>
    /// </summary>
    [Theory]
    [InlineData("function f() local a = 1 + 1 end")]
    [InlineData("function f() local v = hmi_read(\"x\") hmi_write(\"y\", v) end")]
    [InlineData("function f(t) table.insert(t, 1) end")]
    [InlineData("function f() return math.floor(1.5) end")]
    [InlineData("function f(s) return string.len(s) end")]
    public void PlainCode_ProducesNoHits(string code)
        => Assert.Empty(LuaDialect.CheckRuntimeSymbols(code, LuaDialect.Target));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void EmptyCode_ProducesNoHits(string? code)
        => Assert.Empty(LuaDialect.CheckRuntimeSymbols(code, LuaVersion.Lua51));

    // ── 反假阳性：不是引用的地方不许报 ──
    //
    //  这类警告一旦噪声大，用户会在三天内学会无视它 —— 那就是"等于没有"。
    //  下面这几条守的是"显然不是引用"的几种形态。

    [Fact]
    public void StringLiteral_IsNotAReference()
        => Assert.Empty(LuaDialect.CheckRuntimeSymbols(
            "function f() local s = \"bit32.band(1, 3)\" return s end", LuaDialect.Target));

    [Fact]
    public void Comments_AreNotReferences()
    {
        var code = "function f()\n"
                 + "  -- bit32.band(1, 3)\n"
                 + "  --[[ table.pack(1, 2)\n"
                 + "       utf8.len(\"x\") ]]\n"
                 + "  local a = 1\n"
                 + "end";

        Assert.Empty(LuaDialect.CheckRuntimeSymbols(code, LuaDialect.Target));
    }

    /// <summary>
    /// 字段名 / 方法名 / 更长的标识符都**不是**全局引用 ——
    /// <c>x.bit32</c> 前一个字符是 <c>.</c>、<c>x:bit32()</c> 是 <c>:</c>，
    /// 而 <c>bit32x</c> / <c>_bit32</c> 只是名字长得像。
    /// </summary>
    [Theory]
    [InlineData("function f(x) return x.bit32 end")]
    [InlineData("function f(x) x:bit32() end")]
    [InlineData("function f() local bit32x = 1 return bit32x end")]
    [InlineData("function f() local _bit32 = 1 return _bit32 end")]
    public void FieldOrLongerIdentifier_IsNotAGlobalReference(string code)
        => Assert.Empty(LuaDialect.CheckRuntimeSymbols(code, LuaDialect.Target));

    // ── 认出来 + 行号 + 状态 ──

    /// <summary>
    /// 5.4 上确实不可用的符号必须全部认出来，且行号是它真正所在的那一行（不是恒 0、不是恒 1）。
    /// </summary>
    [Fact]
    public void EveryUnavailableSymbol_IsDetected_WithItsLineNumber()
    {
        var unavailable = LuaDialect.RuntimeSymbols
            .Where(s => s.RemovedIn is not null || s.Minimum > LuaDialect.Target)
            .ToList();
        Assert.NotEmpty(unavailable);

        var code = string.Join("\n", unavailable.Select(s => $"local a = {s.Symbol}"));
        var hits = LuaDialect.CheckRuntimeSymbols(code, LuaDialect.Target);

        Assert.Equal(unavailable.Count, hits.Count);
        for (int i = 0; i < hits.Count; i++)
        {
            Assert.Equal(unavailable[i].Symbol, hits[i].Symbol.Symbol);
            Assert.Equal(i + 1, hits[i].Line);
            Assert.Equal(LuaSymbolStatus.Unavailable, hits[i].Status);
        }
    }

    // ── 反向约束：不同目标版本报的集合不同 ──

    /// <summary>
    /// 目标 = Lua 5.1 ⇒ **只报"5.1 之后才有的"**（bit32 / table.pack / table.unpack /
    /// math.type / utf8）；5.1 本来就有的全局 <c>unpack</c> / <c>setfenv</c> 不许报。
    ///
    /// <para>这条正是 <see cref="LuaRuntimeSymbol.RemovedIn"/> 存在的理由：同一个名字在 5.1 可用、
    /// 在 5.4 已移除，只看 <c>Minimum</c> 的模型两边都会判错。</para>
    /// </summary>
    [Fact]
    public void TargetLua51_ReportsOnlyNamesAddedLater()
    {
        // "5.1 上不可用" = 5.1 之后才有的（含 5.2 有、后面又没了的 bit32）
        var expected = LuaDialect.RuntimeSymbols
            .Where(s => s.Minimum > LuaVersion.Lua51
                        && (s.RemovedIn is null || s.RemovedIn > LuaVersion.Lua51))
            .Select(s => s.Symbol)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var hits = LuaDialect.CheckRuntimeSymbols(AllSymbolsAsLines(), LuaVersion.Lua51);

        Assert.Equal(expected, hits.Select(h => h.Symbol.Symbol).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(hits, h => Assert.Equal(LuaSymbolStatus.Unavailable, h.Status));

        foreach (var h in hits)
        {
            var msg = LuaDialect.ExplainHit(h, LuaVersion.Lua51);
            Assert.Contains(LuaDialect.LabelOf(LuaVersion.Lua51), msg);   // 点名真实目标，不写死
            Assert.DoesNotContain("未定", msg);                            // 别把"确定不可用"说成"待定"
        }
    }

    /// <summary>
    /// 反向：目标未定时，文案**不许**说"不存在" —— 那是把"待定"说成了"错误"，
    /// 正是本机制要避免的误报。两个状态的话术必须是分开的。
    /// </summary>
    [Fact]
    public void UnknownTarget_Message_DoesNotClaimTheSymbolIsMissing()
    {
        var h = Assert.Single(LuaDialect.CheckRuntimeSymbols("local a = bit32.band(1, 3)", LuaVersion.Unknown));
        var msg = LuaDialect.ExplainHit(h, LuaVersion.Unknown);

        Assert.Contains("未定", msg);
        Assert.DoesNotContain("不存在", msg);
    }

    // ── 引擎集成：它是**警告**，不是错误 ──

    /// <summary>
    /// **本组最重要的一条**：引用了设备侧可能没有的库 ⇒ 脚本仍然 <c>IsValid</c>，
    /// <c>Errors</c> 必须为空，提示落在 <see cref="ScriptValidationResult.Warnings"/> 里。
    ///
    /// <para>判红就等于把"待定"当成"错误"：设备若是 5.2，这段代码完全正常。</para>
    /// </summary>
    [Fact]
    public void EngineValidate_RuntimeSymbol_IsWarningNotError()
    {
        var engine = new ScriptEngineService();
        var r = engine.Validate(new HmiScript
        {
            Name = "S",
            Code = "function on_click()\n  local a = bit32.band(1, 3)\nend",
        });

        Assert.True(r.IsValid, "「设备上可能没这个库」不许把脚本判红");
        Assert.Empty(r.Errors);

        var w = Assert.Single(r.Warnings);
        // 设备目标已定 5.4，而 bit32 在 5.3 就移除了 ⇒ 判定是"确定不存在"那一态。
        // 仍然只是**警告**：它不影响脚本能否保存，用户可能就是想先写着。
        Assert.Equal(LuaDialect.RuntimeSymbolUnavailableCode, w.Code);
        Assert.Equal(2, w.Line);
    }

    /// <summary>正控：干净脚本两条通道都空。</summary>
    [Fact]
    public void EngineValidate_CleanScript_HasNoWarnings()
    {
        var engine = new ScriptEngineService();
        var r = engine.Validate(new HmiScript
        {
            Name = "S",
            Code = "function on_click()\n  local v = hmi_read(\"x\")\nend",
        });

        Assert.True(r.IsValid);
        Assert.Empty(r.Errors);
        Assert.Empty(r.Warnings);
    }

    /// <summary>
    /// 语法没过时**不跑**运行前检查：此刻用户该看的是那条语法错，
    /// 符号提示只会盖住它（UI 只展示首条）。
    /// </summary>
    [Fact]
    public void EngineValidate_SyntaxError_SkipsRuntimeCheck()
    {
        var engine = new ScriptEngineService();
        var r = engine.Validate(new HmiScript
        {
            Name = "S",
            Code = "function f() local a = bit32.band(1, 3",
        });

        Assert.False(r.IsValid);
        Assert.Contains(r.Errors, e => LuaSyntaxValidator.IsSyntaxError(e.Code));
        Assert.Empty(r.Warnings);
    }

    /// <summary>
    /// 元判据：4 个内置模板不许带出任何运行前提示 ——
    /// 用户点一下「插入模板」就看到黄字，那是产品的锅，不是用户的。
    /// </summary>
    [Fact]
    public void BuiltinTemplates_HaveNoRuntimeWarnings()
    {
        var engine = new ScriptEngineService();

        foreach (var t in ScriptTemplate.Defaults)
        {
            var r = engine.Validate(new HmiScript { Name = t.Name, Code = t.Code });

            Assert.Empty(r.Warnings);
        }
    }

    // ── 负控（同场正控）──

    /// <summary>
    /// 拿真实模板做负控：往模板后追加一行 <c>bit32</c> ⇒ 必须报；原模板 ⇒ 必须不报。
    /// 两条同场，否则"会报"没有信息量（也可能是判据恒报）。
    /// </summary>
    [Fact]
    public void NegativeControl_InjectingBit32IntoATemplate_IsCaught()
    {
        var engine = new ScriptEngineService();
        var code = ScriptTemplate.OnClick.Code;

        // 正控同场
        Assert.Empty(engine.Validate(new HmiScript { Name = "S", Code = code }).Warnings);

        var corrupted = code + "\nlocal x = bit32.band(1, 3)";
        var r = engine.Validate(new HmiScript { Name = "S", Code = corrupted });

        Assert.True(r.IsValid, "注入的是运行前风险，不该变成语法错误");
        Assert.Contains(r.Warnings, w => LuaDialect.IsRuntimeSymbolWarning(w.Code));
    }
}

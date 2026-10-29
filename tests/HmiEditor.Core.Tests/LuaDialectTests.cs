using System;
using System.IO;
using System.Linq;
using HmiEditor.Core.Services;
using HmiEditor.TestSupport;
using Xunit;

namespace HmiEditor.Core.Tests;

/// <summary>
/// Lua 校验的判据。两条主线：
///
/// <para>① **解析器必须与设备同一个版本**（官方 Lua 5.4）。差一个版本就会出两个方向的错：
/// 编辑器把板上合法的写法判成语法错（5.3 的整除 / 位运算、5.4 的属性语法），
/// 或把板上跑不起来的 5.1 遗留物当成可用。</para>
///
/// <para>② **语法合法 ≠ 板上能跑**。<c>setfenv(1, {})</c>、<c>bit32.band</c> 解析器照收，
/// 缺的是运行时的库 —— 这类只能单独扫（<see cref="LuaDialect.CheckRuntimeSymbols"/>），
/// 而且要对"某版本起被移除"的名字也报（<see cref="LuaRuntimeSymbol.RemovedIn"/>）。</para>
/// </summary>
public class LuaDialectTests
{
    // ── 版本：两侧必须一致 ──

    [Fact]
    public void ParserBaseline_AndTarget_AreLua54_MatchingTheRuntime()
    {
        // 运行时链的是 chenge-runtime/third_party/lua-5.4.7（Makefile 的 LUA_DIR）。
        // 这里写死版本字面量是**有意**的：它是"两侧必须一致"的锚点 —— 升级运行时就回来改这里，
        // 同时换 third_party/lua-5.4.7 并重跑 tools/build-lua.sh。
        Assert.Equal(LuaVersion.Lua54, LuaDialect.ParserBaseline);
        Assert.Equal(LuaVersion.Lua54, LuaDialect.Target);
        Assert.Equal("Lua 5.4", LuaDialect.ParserBaselineLabel);
    }

    // ── 语法层：官方解析器必须接受 5.3 / 5.4 的写法 ──

    [Theory]
    [InlineData("local x = 10 // 3")]
    [InlineData("local y = 0x0F & 0x03")]
    [InlineData("local z = 1 << 4")]
    [InlineData("local a <const> = 1")]
    [InlineData("local function f() return 1 end")]
    public void Validate_AcceptsLua54Syntax(string code)
    {
        var result = LuaSyntaxValidator.Validate(code);

        Assert.True(result.IsValid, $"{code} 在官方 5.4 上是合法语法，不该被拒");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_RejectsMissingEnd_WithLineNumber()
    {
        var result = LuaSyntaxValidator.Validate("local function f()\n  local a = 1\n");

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal(LuaSyntaxValidator.SyntaxMissingEndCode, error.Code);
        Assert.True(error.Line >= 2, $"行号应落在缺 end 处附近，实际 {error.Line}");
    }

    [Fact]
    public void Validate_ReportsUnclosedString()
    {
        var result = LuaSyntaxValidator.Validate("local s = \"abc\n");

        Assert.False(result.IsValid);
        Assert.Equal(LuaSyntaxValidator.SyntaxUnfinishedStringCode, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Validate_EmptyCode_IsNotAUserError()
        => Assert.True(LuaSyntaxValidator.Validate("   ").IsValid);

    // ── 语义层：5.4 上已经没有的名字要报出来 ──

    [Theory]
    [InlineData("local t = unpack({1,2})", "全局 unpack")]
    [InlineData("local n = bit32.band(1, 3)", "bit32 库")]
    [InlineData("local ok = setfenv(1, {})", "setfenv")]
    [InlineData("local n = table.getn(t)", "table.getn")]
    [InlineData("local f = loadstring('return 1')", "loadstring")]
    public void RuntimeSymbols_ReportRemovedNames_On54(string code, string name)
    {
        var hits = LuaDialect.CheckRuntimeSymbols(code, LuaDialect.Target);

        Assert.Contains(hits, h => h.Symbol.Name == name && h.Status == LuaSymbolStatus.Unavailable);
    }

    [Fact]
    public void RuntimeSymbols_DoNotFlagNamesThatStillExistOn54()
    {
        // table.unpack / table.pack / utf8 / math.type 在 5.4 都还在 —— 误报比漏报更容易毁掉判据
        var hits = LuaDialect.CheckRuntimeSymbols(
            "local a = table.unpack({1})\n"
            + "local b = table.pack(1)\n"
            + "local c = utf8.len('x')\n"
            + "local d = math.type(1)\n",
            LuaDialect.Target);

        Assert.Empty(hits);
    }

    [Fact]
    public void RuntimeSymbols_IgnoreNamesInsideStringLiterals()
    {
        var hits = LuaDialect.CheckRuntimeSymbols("local s = 'unpack 与 bit32 只是文字'\n", LuaDialect.Target);

        Assert.Empty(hits);
    }

    // ── 事实表与探针不许漂移 ──

    /// <summary>
    /// 事实表里每条 <c>Example</c> 都必须**逐字**出现在
    /// <c>tools/probe-lua-dialect.lua</c> 里。
    ///
    /// <para>为什么值得一条判据：这张表是"设备侧事实"的载体，而事实的唯一来源是那个探针。
    /// 改表不改探针 = 表里出现一条从没被跑过的断言；改探针不改表 = 探针白跑。</para>
    /// </summary>
    [Fact]
    public void FeatureExamples_AllAppearVerbatimInTheProbe()
    {
        var probe = Path.Combine(SourceScanner.RepoRoot, "tools", "probe-lua-dialect.lua");
        Assert.True(File.Exists(probe), $"探针脚本不存在：{probe}");
        var text = File.ReadAllText(probe);

        var missing = LuaDialect.Features
            .Where(f => !text.Contains(f.Example, StringComparison.Ordinal))
            .Select(f => $"{f.Name} ⇒ 「{f.Example}」")
            .ToList();

        Assert.True(missing.Count == 0,
            "以下写法的示例没有逐字出现在探针里（表与探针漂移了）：\n  " + string.Join("\n  ", missing));
    }

    /// <summary>
    /// 非版本单调项（<c>中文标识符</c>）不许被塞一个假的"最小版本"充数：
    /// 它的成因是 locale / 构建配置，不是版本特性。
    /// </summary>
    [Fact]
    public void NonMonotone_StaysOutOfTheVersionModel()
    {
        var nm = Assert.Single(LuaDialect.NonMonotone);
        Assert.Null(nm.Minimum);
        Assert.Equal("中文标识符", nm.Name);
    }
}

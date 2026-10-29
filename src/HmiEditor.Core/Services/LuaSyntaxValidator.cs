using System.Text;
using System.Text.RegularExpressions;

namespace HmiEditor.Core.Services;

/// <summary>
/// Lua 语法校验器 —— 用**官方 Lua 5.4 原生库**做真实语法解析（只 load，不执行任何语句），
/// 把解析失败转成「第 N 行 + 中文说明」。
///
/// <para><b>为什么必须是官方 lua</b>：设备侧运行时链的是 <c>lua-5.4.7</c>（
/// <c>chenge-runtime/third_party</c>）。校验用的解析器与设备差哪怕一个版本，都会出现两个方向的错：
/// 编辑器把板上完全合法的写法判成语法错（5.3 的整除 <c>//</c>、位运算、5.4 的属性语法
/// <c>local x &lt;const&gt;</c>），或把板上跑不起来的遗留物（5.1 的全局 <c>unpack</c>、
/// <c>bit32</c>）当成可用。此前用的 MoonSharp 只实现到 Lua 5.2，两个方向都踩过。</para>
///
/// <para><b>只做语法层</b>：<c>luaL_loadbuffer</c> 不执行任何语句。语义层的东西（引用了板上
/// 不存在的库 / 字段）它看不见 —— 那由 <see cref="LuaDialect.CheckRuntimeSymbols"/> 单独扫描，
/// 组装点在 <see cref="ScriptEngineService.Validate"/>。**别把符号检查塞进本类**：混进来之后
/// "语法错"和"设备侧风险"就再也分不开了。</para>
///
/// <para>原生库由 <c>tools/build-lua.sh</c> 编自 <c>third_party/lua-5.4.7</c>（与设备同一份源码，MIT）。
/// 加载失败时返回一条"校验不可用"的警告 —— 不判红（那不是用户的错），也不假装通过。</para>
/// </summary>
public static class LuaSyntaxValidator
{
    /// <summary>
    /// 语法错的 Code **族前缀** —— 所有细分诊断码都以它开头（区别于模型级判据
    /// <c>EMPTY_NAME</c>/<c>EMPTY_CODE</c>/<c>NO_FUNCTION</c>，那三个不属于本族）。
    ///
    /// <para><b>为什么是"前缀 + 细分码"而不是一个笼统的 <c>LUA_SYNTAX_ERROR</c></b>：
    /// 界面要把诊断**原样显示给用户**，而"代码不完整可能缺 end""字符串没闭合"
    /// "这是 5.3 的运算符、此处应视为误报"是三句完全不同的话。笼统一个码就只能把区分塞进
    /// <c>ScriptError.Args</c>，而 <c>Args</c> 是**填模板用的**、不是给程序分派用的
    /// （两件事挤在一个通道里，消费方迟早要猜）。细分之后每个码各自对应一条字典模板，
    /// 也各自可被分派（将来的快速修复、AI 提示都以它为准）。</para>
    ///
    /// <para>判族一律用 <see cref="IsSyntaxError"/>，**不要**拿 Code 去 <c>==</c> 某个具体值
    /// —— 那样一细分就静默失效。</para>
    /// </summary>
    public const string SyntaxErrorCodePrefix = "LUA_SYNTAX_";

    /// <summary>该 Code 是否属于语法错族。</summary>
    public static bool IsSyntaxError(string? code)
        => code is not null && code.StartsWith(SyntaxErrorCodePrefix, StringComparison.Ordinal);

    /// <summary>
    /// 校验器自身不可用（原生 lua 库没加载上）。**不属于 <c>LUA_SYNTAX_</c> 族** ——
    /// 它说的是"编辑器缺东西"，不是"你的脚本写错了"，所以走警告通道。
    /// </summary>
    public const string LuaCheckUnavailableCode = "LUA_CHECK_UNAVAILABLE";

    // 解析器报错的分类（按官方 lua 5.4 的报错文本判定，**顺序即优先级**：具体在前）。
    public const string SyntaxMissingEndCode = SyntaxErrorCodePrefix + "MISSING_END";
    public const string SyntaxUnfinishedLongCode = SyntaxErrorCodePrefix + "UNFINISHED_LONG";
    public const string SyntaxUnfinishedStringCode = SyntaxErrorCodePrefix + "UNFINISHED_STRING";
    public const string SyntaxUnexpectedSymbolCode = SyntaxErrorCodePrefix + "UNEXPECTED_SYMBOL";
    public const string SyntaxMissingEqualsCode = SyntaxErrorCodePrefix + "MISSING_EQUALS";
    public const string SyntaxMissingNameCode = SyntaxErrorCodePrefix + "MISSING_NAME";
    public const string SyntaxExpectedCode = SyntaxErrorCodePrefix + "EXPECTED";
    public const string SyntaxGenericCode = SyntaxErrorCodePrefix + "GENERIC";

    /// <summary>
    /// 校验 Lua 源码。空内容视为**合法**（「内容不能为空」由调用方判，见 <c>EMPTY_CODE</c>）。
    /// </summary>
    public static ScriptValidationResult Validate(string? code)
    {
        var result = new ScriptValidationResult();
        if (string.IsNullOrWhiteSpace(code)) return result;

        try
        {
            // 只解析、不执行：luaL_loadbuffer 做到"函数原型"为止，没有 pcall 就没有执行。
            if (!NativeLua.TryCheck(code, out var raw) && !string.IsNullOrEmpty(raw))
            {
                result.IsValid = false;
                result.Errors.Add(BuildSyntaxError(code, raw));
            }
        }
        catch (LuaNativeUnavailableException ex)
        {
            // 校验器自己坏了，不能因此判用户代码错：给一条警告放行
            result.Warnings.Add(new ScriptError
            {
                Code = LuaCheckUnavailableCode,
                Message = "语法校验不可用：编辑器缺少原生 lua 库",
                Args = new object[] { ex.Message },
            });
        }

        return result;
    }

    /// <summary>
    /// 源码里是否存在函数定义。
    ///
    /// <para>判据是「**剥掉注释后**仍能查到 <c>function</c> 关键字」。裸
    /// <c>Contains("function ")</c> 会被注释里的示例骗过 —— 实测
    /// <c>-- function ghost()\nlocal a = 1</c> 在裸判据下"有函数"，剥注释后才如实报「没有」。</para>
    ///
    /// <para><b>已知假阴性</b>：字符串字面量里的 <c>"function foo()"</c> 仍会被算作有函数
    /// （本方法不改写字符串内容）。危害小 —— 运行时 <c>lua_getglobal</c> 找不到函数会自己报错。</para>
    /// </summary>
    public static bool HasFunctionDefinition(string? code)
        => !string.IsNullOrWhiteSpace(code)
           && Regex.IsMatch(StripComments(code), @"\bfunction\b", RegexOptions.Singleline);

    /// <summary>
    /// 剥掉 Lua 注释：行注释 <c>--</c> 到行尾、块注释 <c>--[[ … ]]</c> 与 <c>--[=[ … ]=]</c>。
    /// <b>换行符一律保留</b>（行号不漂移），字符串字面量原样保留。
    /// </summary>
    public static string StripComments(string code)
    {
        if (string.IsNullOrEmpty(code)) return code ?? "";

        var sb = new StringBuilder(code.Length);
        int i = 0, n = code.Length;

        while (i < n)
        {
            char c = code[i];

            // 字符串字面量：整体抄过去（含转义）
            if (c is '"' or '\'')
            {
                char quote = c;
                sb.Append(c);
                i++;
                while (i < n)
                {
                    if (code[i] == '\\' && i + 1 < n)
                    {
                        sb.Append(code[i]).Append(code[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(code[i]);
                    if (code[i] == quote) { i++; break; }
                    i++;
                }
                continue;
            }

            if (c == '-' && i + 1 < n && code[i + 1] == '-')
            {
                // 块注释：--[ 后面跟 N 个 '=' 再跟 '['
                int lb = i + 2, eq = 0;
                while (lb + eq < n && code[lb + eq] == '=') eq++;
                if (lb + eq + 1 < n && code[lb + eq] == '[' && code[lb + eq + 1] == '[')
                {
                    string closer = "]" + new string('=', eq) + "]";
                    int end = code.IndexOf(closer, lb + eq + 2, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + closer.Length;
                    // 保留区间内的换行，避免行号漂移
                    for (int k = i; k < stop; k++)
                        if (code[k] == '\n') sb.Append('\n');
                    i = stop;
                    continue;
                }

                // 行注释：到行尾（不消费换行）
                while (i < n && code[i] != '\n') i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // ── 错误消息解析与中文化 ──

    /// <summary>
    /// 官方 lua 的报错格式（chunkname 用 <c>=script</c>，所以前缀是字面名，不含 <c>[string "..."]</c>）：
    /// <code>script:2: unexpected symbol near &lt;eof&gt;
    ///script:3: 'end' expected (to close 'function' at line 1) near &lt;eof&gt;</code>
    /// 取**最后一个** <c>:行号:</c> —— 源码片段里本身可能带冒号（实测 <c>[string "a:1:"]</c>）。
    /// </summary>
    private static readonly Regex LuaErrorRe = new(
        @"^.*?:(?<line>\d+):\s*(?<msg>.+)$", RegexOptions.Singleline);

    private static ScriptError BuildSyntaxError(string code, string rawError)
    {
        var m = LuaErrorRe.Match(rawError);

        int line;
        string raw;

        if (m.Success)
        {
            line = int.Parse(m.Groups["line"].Value);
            raw = m.Groups["msg"].Value.Trim();
        }
        else
        {
            // 兜底：格式变了也不谎报第 0 行 —— 取总行数（解析器报错多落在末尾附近）
            line = Math.Max(1, code.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length);
            raw = rawError.Trim();
        }

        var (diagCode, args, message) = Diagnose(raw);

        return new ScriptError
        {
            Line = line,
            // 官方 lua 只给行号，不给列号 —— 不编一个假的
            Column = 0,
            Code = diagCode,
            // 不含行号 —— Line 已经是结构化字段，由消费方统一渲染（见 ScriptError 的类型注释）
            Message = message,
            Args = args.Length > 0 ? args : null,   // 空数组与 null 等价，统一成 null
        };
    }

    /// <summary>
    /// 把解析器的英文报错**分类**成「诊断 Code + 模板实参 + 中文回落」。
    ///
    /// <para>这些报错对用户是两件不同的事：**确实写漏了**（缺 <c>end</c> / 缺引号 / 缺 <c>=</c>）
    /// 该催他改；**解析器说不上来的**（兜底）只能原样给出。混成一句"语法错误"就把两者抹平了，
    /// 而界面只有一行位置。</para>
    ///
    /// <para><b>匹配顺序即优先级，具体在前</b>：<c>'end' expected</c> 也含 "expected"、
    /// <c>unfinished long string</c> 也含 "unfinished string" —— 顺序一乱就静默归错类，
    /// 而且错得看不出来（文案仍然"合理"）。</para>
    /// </summary>
    private static (string Code, object[] Args, string Message) Diagnose(string raw)
    {
        if (raw.Contains("unfinished long string", StringComparison.Ordinal) ||
            raw.Contains("unfinished long comment", StringComparison.Ordinal))
            return (SyntaxUnfinishedLongCode, Array.Empty<object>(), "长字符串 / 长注释没有闭合（缺少 ]]）");

        if (raw.Contains("unfinished string", StringComparison.Ordinal))
            return (SyntaxUnfinishedStringCode, Array.Empty<object>(), "字符串没有闭合（缺少结尾的引号）");

        if (raw.Contains("'end' expected", StringComparison.Ordinal))
            return (SyntaxMissingEndCode, Array.Empty<object>(), "代码不完整，缺少 end");

        if (raw.Contains("malformed number", StringComparison.Ordinal))
            return (SyntaxUnexpectedSymbolCode, new object[] { raw }, $"数字写法不合法：{raw}");

        if (raw.Contains("'=' expected", StringComparison.Ordinal))
            return (SyntaxMissingEqualsCode, Array.Empty<object>(), "缺少赋值符号 '='");

        if (raw.Contains("'<name>' expected", StringComparison.Ordinal))
            return (SyntaxMissingNameCode, Array.Empty<object>(), "缺少名称（变量名或函数名）");

        if (raw.Contains("unexpected symbol", StringComparison.Ordinal))
            return (SyntaxUnexpectedSymbolCode, new object[] { raw }, $"出现了意外的符号：{raw}");

        if (raw.Contains("expected", StringComparison.Ordinal))
            return (SyntaxExpectedCode, new object[] { raw }, $"缺少必要的语法元素：{raw}");

        return (SyntaxGenericCode, new object[] { raw }, $"存在语法问题：{raw}");
    }
}

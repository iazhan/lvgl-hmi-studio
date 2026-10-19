using System.IO;
using System.Text.Json;

namespace HmiEditor.Device.Build;

/// <summary>
/// SDK 里声明的交叉工具链（<c>&lt;sdk&gt;/toolchain/toolchain.json</c>）。
///
/// <para><b>为什么要有它</b>：交叉编译原本硬编码 buildroot 的
/// <c>arm-buildroot-linux-gnueabi-gcc</c>，而那是 x86-64 Linux ELF + glibc 的产物 ——
/// Windows 上根本无法执行。而 <c>.so</c> 的链接**只需要头文件**（实测 DT_NEEDED 只有
/// libc.so.6、未定义符号全是宿主提供的），所以同一份运行时源码 + ARM sysroot 头文件配上
/// 原生 clang 就能产出 ABI 等价的 <c>.so</c>。清单把"用哪个编译器、要补哪些 flag"变成数据，
/// 于是同一份 SDK 打包脚本可以出 Linux 版和 Windows 版，构建器不必为每个平台写分支。</para>
///
/// <para><b>实测依据</b>（clang 19.1.1 + buildroot ARM sysroot，与 gcc 版逐符号比对）：
/// ELF 头 / DT_NEEDED / 版本需求条目 / 导出符号集 / 未定义符号集全部一致。
/// 两个 clang 特有的坑已固化进本类的 flag 生成：</para>
/// <list type="number">
///   <item>必须显式 <c>-mfloat-abi=soft</c>（gcc 靠 spec 隐式，clang 不认）；</item>
///   <item>必须链 <c>-lgcc</c>，否则 <c>__aeabi_*</c> 软浮点例程会留在未定义符号里
///        （gcc 从自带 libgcc 静态解决，clang 默认不链）。</item>
/// </list>
/// </summary>
public sealed record KinSealSdkToolchain
{
    /// <summary>编译器种类：<c>gcc</c>（buildroot 交叉 gcc）或 <c>clang</c>（LLVM，跨平台）。</summary>
    public string Kind { get; init; } = "gcc";

    /// <summary>编译器可执行文件名（位于 <c>&lt;toolchain&gt;/bin/</c> 下）。</summary>
    public string Cc { get; init; } = "";

    /// <summary>clang 的目标三元组（如 <c>armv5te-linux-gnueabi</c>）。gcc 留空。</summary>
    public string Target { get; init; } = "";

    /// <summary>浮点 ABI（clang 用，<c>-mfloat-abi=</c> 的值）。gcc 留空。</summary>
    public string FloatAbi { get; init; } = "";

    /// <summary>ARM sysroot 目录名（相对 toolchain 根；clang 需要，gcc 留空）。</summary>
    public string Sysroot { get; init; } = "";

    /// <summary>链接器可执行文件名（位于 <c>&lt;toolchain&gt;/bin/</c> 下）。
    /// clang 用它拼 <c>--ld-path=</c> —— 必须是**路径**：<c>-fuse-ld=</c> 只认名字
    /// （<c>lld</c>/<c>gold</c>…）且只在 PATH 里找，而 SDK 的 bin/ 不在 PATH 上，
    /// 给名字会直接报 <c>invalid linker name</c>。gcc 留空。</summary>
    public string Ld { get; init; } = "";

    /// <summary>libgcc 所在目录名（相对 toolchain 根）。</summary>
    public string LibDir { get; init; } = "lib";

    /// <summary>libgcc 的链接名（<c>-l</c> 后面的部分）。</summary>
    public string LibName { get; init; } = "gcc";

    /// <summary>
    /// 链接时是否加 <c>-nostdlib</c>。clang 必须加：驱动默认会找 <c>-lgcc</c>，
    /// 而它自己的工具链目录里没有（那份在 buildroot 的 <c>lib/gcc/…</c> 下），不关掉就链接失败。
    /// </summary>
    public bool NoStdlib { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从 <c>&lt;toolchain&gt;/toolchain.json</c> 读取；文件不存在或读坏返回 null。</summary>
    public static KinSealSdkToolchain? Load(string toolchainDir)
    {
        if (string.IsNullOrWhiteSpace(toolchainDir)) return null;
        var path = Path.Combine(toolchainDir, "toolchain.json");
        if (!File.Exists(path)) return null;
        try
        {
            return Validate(JsonSerializer.Deserialize<KinSealSdkToolchain>(File.ReadAllText(path), Json));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;      // 读坏的清单按"没有"处理，回落到硬编码 gcc 行为
        }
    }

    private static KinSealSdkToolchain? Validate(KinSealSdkToolchain? t)
    {
        if (t is null || string.IsNullOrWhiteSpace(t.Cc)) return null;
        var kind = t.Kind.Trim().ToLowerInvariant();
        if (kind is not ("gcc" or "clang")) return null;
        if (kind == "clang" && string.IsNullOrWhiteSpace(t.Target)) return null;
        return kind == t.Kind ? t : t with { Kind = kind };
    }

    /// <summary>编译器可执行文件的绝对路径（bin 下，Windows 补 .exe）。</summary>
    public string ExecutablePath(string toolchainDir)
    {
        var name = Cc;
        if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return Path.GetFullPath(Path.Combine(toolchainDir, "bin", name));
    }

    /// <summary>编译期附加 flag（clang 的 target / float-abi / sysroot；gcc 为空，由 spec 隐式）。</summary>
    public IReadOnlyList<string> CompileFlags(string toolchainDir)
    {
        var flags = new List<string>();
        if (!string.Equals(Kind, "clang", StringComparison.OrdinalIgnoreCase)) return flags;

        if (!string.IsNullOrWhiteSpace(Target)) flags.Add($"--target={Target}");
        if (!string.IsNullOrWhiteSpace(FloatAbi)) flags.Add($"-mfloat-abi={FloatAbi}");
        if (!string.IsNullOrWhiteSpace(Sysroot))
            flags.Add($"--sysroot={Path.GetFullPath(Path.Combine(toolchainDir, Sysroot))}");
        return flags;
    }

    /// <summary>
    /// 链接期完整参数：<c>-shared -nostartfiles</c> 之外，clang 还要
    /// <c>-nostdlib -fuse-ld=&lt;ld&gt; -L&lt;libDir&gt; -l&lt;libName&gt;</c>。
    /// </summary>
    public IReadOnlyList<string> LinkFlags(string toolchainDir, string outputPath, string objectPath)
    {
        // 链接同样要带 target/sysroot：clang 驱动按 --target 选 linker 与库搜索路径，
        // 漏了会按宿主架构（x86-64）链接，报 "incompatible with elf_x86_64"。
        var flags = new List<string>(CompileFlags(toolchainDir));
        flags.Add("-shared");
        if (NoStdlib) flags.Add("-nostdlib");
        flags.Add("-nostartfiles");
        // -nostdlib 会把 -lc 一起关掉，这里显式补回：DT_NEEDED 必须含 libc.so.6，
        // 与 gcc 版一致。否则生成代码一旦调用 libc 函数（memcpy/strlen/…），
        // 那些符号会变成未定义，而宿主不一定导出它们 —— 实测两边符号集因此差过 5 个。
        if (string.Equals(Kind, "clang", StringComparison.OrdinalIgnoreCase)) flags.Add("-lc");

        if (string.Equals(Kind, "clang", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(Ld))
            {
                var ldPath = Path.GetFullPath(Path.Combine(toolchainDir, "bin", Ld));
                flags.Add($"--ld-path={ldPath}");
            }
            flags.Add($"-L{Path.GetFullPath(Path.Combine(toolchainDir, LibDir))}");
            if (!string.IsNullOrWhiteSpace(LibName)) flags.Add($"-l{LibName}");
        }

        flags.Add("-o");
        flags.Add(outputPath);
        flags.Add(objectPath);
        return flags;
    }
}

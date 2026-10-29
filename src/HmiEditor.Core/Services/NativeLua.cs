using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace HmiEditor.Core.Services;

/// <summary>
/// 官方 Lua 5.4 的**只解析**封装：<c>luaL_loadbuffer</c> 做词法 + 语法分析并构建函数原型，
/// **不执行任何语句**（没有 <c>lua_pcall</c>、没有 <c>luaL_dostring</c>）。
///
/// <para><b>为什么用原生 lua 而不是 C# 解析器</b>：设备侧运行时链的就是
/// <c>chenge-runtime/third_party/lua-5.4.7</c>。校验要与设备**同一个解释器**才有意义 ——
/// 差一个小版本都会出现「编辑器说合法、板上报错」或反过来的情况。原生库来自
/// <c>third_party/lua-5.4.7</c>（同一份源码，MIT），由 <c>tools/build-lua.sh</c> 编成
/// <c>liblua54.so</c> / <c>lua54.dll</c>。</para>
///
/// <para><b>找不到原生库时</b>抛 <see cref="LuaNativeUnavailableException"/>，由
/// <see cref="LuaSyntaxValidator.Validate"/> 转成一条"校验不可用"的警告 —— 不判红，
/// 也不假装通过。</para>
/// </summary>
internal static class NativeLua
{
    private const string LibraryName = "lua54";

    private static readonly string FileName =
        OperatingSystem.IsWindows() ? "lua54.dll" : "liblua54.so";

    private static readonly object ResolverGate = new();
    private static bool _resolverInstalled;

    static NativeLua() => EnsureResolver();

    /// <summary>
    /// 让 DllImport("lua54") 从应用目录附近找库：发布目录、<c>native/</c>、
    /// <c>runtimes/&lt;rid&gt;/native/</c> 都试一遍（开发机与安装目录布局不同）。
    /// </summary>
    private static void EnsureResolver()
    {
        lock (ResolverGate)
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeLua).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // 同一程序集只能设一次解析器；已被别处设过就沿用那一个，不覆盖
            }
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, LibraryName, StringComparison.Ordinal)) return IntPtr.Zero;

        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        foreach (var dir in new[]
                 {
                     AppContext.BaseDirectory,
                     Path.Combine(AppContext.BaseDirectory, "native"),
                     Path.Combine(AppContext.BaseDirectory, "native", rid),
                     Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native"),
                 })
        {
            var path = Path.Combine(dir, FileName);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle)) return handle;
        }

        return IntPtr.Zero;   // 交给默认搜索（系统路径 / LD_LIBRARY_PATH）
    }

    /// <summary>
    /// 解析一段源码。<paramref name="error"/> 为官方 lua 的原始消息（形如
    /// <c>script:2: unexpected symbol near &lt;eof&gt;</c>）；语法合法时返回 <see langword="true"/>。
    /// </summary>
    public static bool TryCheck(string code, out string? error)
    {
        error = null;
        EnsureResolver();

        IntPtr state;
        try
        {
            state = luaL_newstate();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new LuaNativeUnavailableException(
                $"原生 lua 库加载失败（找的是 {FileName}）：{ex.Message}", ex);
        }

        if (state == IntPtr.Zero)
            throw new LuaNativeUnavailableException("luaL_newstate 返回空状态");

        try
        {
            var bytes = Encoding.UTF8.GetBytes(code);
            // 用 luaL_loadbufferx 而不是 luaL_loadbuffer：后者在 lauxlib.h 里是**宏**
            // （转调 loadbufferx 并补 NULL mode），.so 里没有这个符号，DllImport 会报 EntryPointNotFound。
            // chunkname 用 "=script"：消息前缀就是 "script:2: ..."，不带 [string "..."] 包裹。
            var rc = luaL_loadbufferx(state, bytes, (UIntPtr)(uint)bytes.Length, "=script", null);
            if (rc == 0) return true;

            error = ReadError(state);
            return false;
        }
        finally
        {
            lua_close(state);
        }
    }

    private static string ReadError(IntPtr state)
    {
        var ptr = lua_tolstring(state, -1, out var length);
        if (ptr == IntPtr.Zero) return "";

        try
        {
            return Marshal.PtrToStringUTF8(ptr, checked((int)length.ToUInt32())) ?? "";
        }
        catch (OverflowException)
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "";
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr luaL_newstate();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void lua_close(IntPtr state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int luaL_loadbufferx(
        IntPtr state, byte[] buffer, UIntPtr size,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? mode);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr lua_tolstring(IntPtr state, int index, out UIntPtr length);
}

/// <summary>原生 lua 库不可用（未随包 / 架构不符 / 文件损坏）。</summary>
internal sealed class LuaNativeUnavailableException : Exception
{
    public LuaNativeUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

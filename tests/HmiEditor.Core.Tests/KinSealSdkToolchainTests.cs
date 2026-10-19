using System.Collections.Generic;
using System.IO;
using HmiEditor.Device.Build;
using HmiEditor.TestSupport;
using Xunit;

namespace HmiEditor.Core.Tests;

/// <summary>
/// 工具链清单（<c>toolchain/toolchain.json</c>）的解析与 flag 生成。
///
/// <para><b>为什么钉得这么细</b>：这份清单是"同一份 SDK 源码出 Linux/Windows 两个平台变体"
/// 的唯一开关，而 clang 分支的 flag 全是实测踩出来的 —— 漏一个，产物要么不是 ARM soft-float、
/// 要么留一堆 <c>__aeabi_*</c> 未定义符号、要么 DT_NEEDED 缺 libc。这些断言把结论固定住，
/// 免得以后"简化"flag 时把坑一个个踩回来。</para>
/// </summary>
public class KinSealSdkToolchainTests
{
    private static KinSealSdkToolchain WriteManifest(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "toolchain.json"), json);
        return KinSealSdkToolchain.Load(dir)!;
    }

    [Fact]
    public void Load_GccManifest_HasNoExtraFlags()
    {
        var dir = Path.Combine(TestTempDir.NewDir("tc"), "toolchain");
        var tc = WriteManifest(dir, """{ "kind": "gcc", "cc": "arm-buildroot-linux-gnueabi-gcc" }""");

        Assert.Equal("gcc", tc.Kind);
        Assert.Empty(tc.CompileFlags(dir));      // gcc 靠 spec 隐式，不注入任何 flag

        var link = tc.LinkFlags(dir, "out.so", "a.o");
        Assert.Equal(new[] { "-shared", "-nostartfiles", "-o", "out.so", "a.o" }, link);
    }

    [Fact]
    public void Load_ClangManifest_FlagsCarryEverythingNeeded()
    {
        var dir = Path.Combine(TestTempDir.NewDir("tc"), "toolchain");
        var tc = WriteManifest(dir, """
            {
              "kind": "clang", "cc": "clang", "target": "armv5te-linux-gnueabi",
              "floatAbi": "soft", "sysroot": "sysroot", "ld": "ld.lld",
              "libDir": "lib", "libName": "gcc", "noStdlib": true
            }
            """);

        Assert.Equal("clang", tc.Kind);

        var compile = tc.CompileFlags(dir);
        Assert.Contains("--target=armv5te-linux-gnueabi", compile);
        Assert.Contains("-mfloat-abi=soft", compile);
        Assert.Contains(compile, f => f.StartsWith("--sysroot="));
        Assert.Contains(compile, f => f.EndsWith("sysroot"));   // 指向 toolchain 内的 sysroot

        var link = tc.LinkFlags(dir, "out.so", "a.o");
        Assert.Contains(link, f => f.StartsWith("--ld-path="));
        Assert.Contains(link, f => f.EndsWith(Path.Combine("bin", "ld.lld")));
        Assert.Contains(link, f => f.StartsWith("-L"));
        Assert.Contains("-lgcc", link);        // 少了它 __aeabi_* 会留在未定义符号里
        Assert.Contains("-lc", link);          // 少了它 DT_NEEDED 缺 libc.so.6
        Assert.Contains("-nostdlib", link);    // 少了它驱动找不到 -lgcc 直接链接失败
        Assert.Contains("-nostartfiles", link);
    }

    [Theory]
    [InlineData("""{ "kind": "zig", "cc": "zig" }""")]                       // 不认识的种类
    [InlineData("""{ "kind": "gcc" }""")]                                  // 缺 cc
    [InlineData("""{ "kind": "clang", "cc": "clang" }""")]                  // clang 缺 target
    public void Load_RejectsInvalidManifest(string json)
    {
        var dir = Path.Combine(TestTempDir.NewDir("tc"), "toolchain");
        WriteManifest(dir, json);
        Assert.Null(KinSealSdkToolchain.Load(dir));
    }

    [Fact]
    public void Load_MissingOrBrokenManifest_ReturnsNull()
    {
        var missing = Path.Combine(TestTempDir.NewDir("tc"), "toolchain");
        Assert.Null(KinSealSdkToolchain.Load(missing));      // 文件不存在

        Directory.CreateDirectory(missing);
        File.WriteAllText(Path.Combine(missing, "toolchain.json"), "{ 这不是 json");
        Assert.Null(KinSealSdkToolchain.Load(missing));      // 读坏 → 按"没有"处理
    }

    [Fact]
    public void Load_KindIsCaseInsensitive()
    {
        var dir = Path.Combine(TestTempDir.NewDir("tc"), "toolchain");
        var tc = WriteManifest(dir, """{ "kind": "CLANG", "cc": "clang", "target": "armv5te-linux-gnueabi" }""");
        Assert.Equal("clang", tc.Kind);
    }

    [Fact]
    public void ExecutablePath_PointsIntoBin()
    {
        var dir = Path.Combine(TestTempDir.NewDir("tc"), "toolchain");
        var tc = WriteManifest(dir, """{ "kind": "gcc", "cc": "arm-buildroot-linux-gnueabi-gcc" }""");

        var exe = tc.ExecutablePath(dir);
        Assert.EndsWith(Path.Combine("toolchain", "bin", "arm-buildroot-linux-gnueabi-gcc"), exe);
    }
}

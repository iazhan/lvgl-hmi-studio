using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using HmiEditor.Device.Build;
using HmiEditor.TestSupport;
using Xunit;

namespace HmiEditor.Core.Tests;

/// <summary>
/// kinseal SDK 的「清单 → 校验 → 解压 → 落盘」与随包安装。
///
/// <para><b>为什么要逐条钉</b>：这条链上每一步失败都表现为"编译时找不到头文件/编译器"，
/// 与真正的原因隔了很远。这里直接断言"坏包不许落盘、越界条目当场拒绝、装好之后构建器能认出来"。</para>
///
/// <para><b>测试隔离</b>：安装目标固定在 <c>AppPaths.DataRoot/sdk/kinseal</c>，而
/// <c>tests/_shared/TestAppDataRootSetup.cs</c> 已把数据根重定向到仓库内
/// <c>.hmi-test-tmp/appdata</c>；每个用例结束都删掉该目录，避免与其它程序集的
/// 「没有 SDK → 应为 null」断言互相干扰。</para>
/// </summary>
public class KinSealSdkInstallerTests : IDisposable
{
    private const string GccName = "arm-buildroot-linux-gnueabi-gcc";

    // 每条用例一个空 SDK 根，装/删都只影响自己
    private readonly SdkRootSandbox _sdkRoot = new();

    public void Dispose() => _sdkRoot.Dispose();

    private static string SdkRoot => KinSealSdkInstaller.SdkRoot;

    /// <summary>用例收尾：把装好的 SDK 删掉，恢复"未安装"状态。</summary>
    private static void Cleanup()
    {
        try
        {
            if (Directory.Exists(SdkRoot)) Directory.Delete(SdkRoot, recursive: true);
            var parent = Path.GetDirectoryName(SdkRoot);
            if (parent is not null && Directory.Exists(parent))
            {
                foreach (var leftover in Directory.EnumerateDirectories(parent, "kinseal.staging-*").ToList())
                    Directory.Delete(leftover, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void AddFile(TarWriter writer, string name, string content)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
        };
        writer.WriteEntry(entry);
    }

    /// <summary>造一个"内容合格"的 SDK 归档；<paramref name="wrapper"/> 模拟单层包裹目录。</summary>
    private static string BuildArchive(string workDir, string? wrapper = null, Action<TarWriter>? extra = null)
    {
        var path = Path.Combine(workDir, "kinseal-sdk-1.0.0.tar.gz");
        var prefix = string.IsNullOrEmpty(wrapper) ? "" : wrapper!.TrimEnd('/') + "/";

        using (var file = File.Create(path))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            AddFile(writer, prefix + "runtime/src/runtime/hmi_ui_api.h", "/* api */\n");
            AddFile(writer, prefix + "runtime/config/lv_conf.h", "#define LV_COLOR_DEPTH 32\n");
            AddFile(writer, prefix + "runtime/third_party/lvgl-9.5.0/lvgl.h", "/* lvgl */\n");
            AddFile(writer, prefix + "runtime/third_party/lvgl-9.5.0/CMakeLists.txt", "cmake_minimum_required(VERSION 3.10)\n");
            AddFile(writer, prefix + "toolchain/bin/" + GccName, "#!/bin/sh\nexit 0\n");
            extra?.Invoke(writer);
        }
        return path;
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // ───────────────────────── 清单 ─────────────────────────

    [Fact]
    public void Manifest_ParsesAndNormalizesSha()
    {
        var manifest = KinSealSdkManifest.Parse("""
            {
              "schemaVersion": 1,
              "version": "1.0.0",
              "url": "",
              "sha256": "  ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789  ",
              "sizeBytes": 123
            }
            """);

        Assert.Equal("1.0.0", manifest.Version);
        // 大小写归一 + 去空白：清单是人写的/脚本生成的，校验时不能因大小写而不等
        Assert.Equal(string.Concat(Enumerable.Repeat("abcdef0123456789", 4)), manifest.Sha256);
        Assert.Equal(123, manifest.SizeBytes);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "version": "1", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "1", "sha256": "abc" }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "1", "url": "ftp://x/y.tgz", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }""")]
    public void Manifest_RejectsInvalidFields(string json)
    {
        Assert.Throws<InvalidDataException>(() => KinSealSdkManifest.Parse(json));
    }

    // ───────────────────────── 本地安装 ─────────────────────────

    [Fact]
    public async Task InstallFromArchive_ValidPackage_InstallsAndBuilderFindsIt()
    {
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var archive = BuildArchive(work);
            var sha = Sha256Of(archive);

            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(archive, sha, "1.0.0");

            Assert.True(result.Success, result.Error);
            Assert.True(result.Installed);
            Assert.Equal("1.0.0", result.Version);
            Assert.True(KinSealSdkInstaller.IsInstalled());
            Assert.Equal("1.0.0", KinSealSdkInstaller.ReadState()?.Version);
            Assert.True(File.Exists(Path.Combine(SdkRoot, "toolchain", "bin", GccName)));

            // 构建器的发现逻辑必须认它（否则装好了也白装）：运行时根 = <SdkRoot>/runtime
            Assert.Equal(Path.Combine(SdkRoot, "runtime"), F1C100SSoBuilder.FindRuntimeDir(null));
            Assert.Equal(Path.Combine(SdkRoot, "toolchain", "bin", GccName),
                F1C100SSoBuilder.FindToolchainGcc(null));
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    [Fact]
    public async Task InstallFromArchive_WrappedTopLevel_IsLifted()
    {
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var archive = BuildArchive(work, wrapper: "kinseal-sdk-1.0.0");

            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(archive, Sha256Of(archive), "1.0.0");

            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(Path.Combine(SdkRoot, "runtime", "src", "runtime", "hmi_ui_api.h")));
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    [Fact]
    public async Task InstallFromArchive_ShaMismatch_LeavesNothingBehind()
    {
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var archive = BuildArchive(work);

            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(archive, new string('0', 64), "1.0.0");

            Assert.False(result.Success);
            Assert.Contains("SHA-256", result.Error);
            Assert.False(Directory.Exists(SdkRoot));
            Assert.False(KinSealSdkInstaller.IsInstalled());
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    [Fact]
    public async Task InstallFromArchive_MissingLvglTree_Rejected()
    {
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var path = Path.Combine(work, "partial.tar.gz");
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                AddFile(writer, "runtime/src/runtime/hmi_ui_api.h", "/* api */\n");
                AddFile(writer, "toolchain/bin/" + GccName, "#!/bin/sh\n");
            }

            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(path, Sha256Of(path), "1.0.0");

            Assert.False(result.Success);
            Assert.Contains("不完整", result.Error);
            Assert.False(Directory.Exists(SdkRoot));
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    // ───────────────────────── 解压安全 ─────────────────────────

    [Fact]
    public async Task InstallFromArchive_PathTraversal_Rejected()
    {
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var archive = BuildArchive(work, extra: w => AddFile(w, "runtime/../../evil.txt", "pwned"));

            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(archive, Sha256Of(archive), "1.0.0");

            Assert.False(result.Success);
            Assert.Contains("越界", result.Error);
            Assert.False(Directory.Exists(SdkRoot));
            Assert.False(File.Exists(Path.Combine(work, "..", "evil.txt")));
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    [Fact]
    public async Task InstallFromArchive_SymlinkInsideRoot_IsRestored()
    {
        // buildroot 的交叉 gcc 本身就是 `-> toolchain-wrapper` 的软链：链接没还原 = 装完不能用
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var archive = BuildArchive(work, extra: w => w.WriteEntry(new PaxTarEntry(
                TarEntryType.SymbolicLink, "toolchain/bin/arm-buildroot-linux-gnueabi-gcc-link")
            {
                LinkName = GccName,
            }));

            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(archive, Sha256Of(archive), "1.0.0");

            Assert.True(result.Success, result.Error);
            var link = Path.Combine(SdkRoot, "toolchain", "bin", "arm-buildroot-linux-gnueabi-gcc-link");
            Assert.True(File.Exists(link));
            Assert.NotNull(new FileInfo(link).LinkTarget);   // 真的是链接，不是留了个空文件
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    [Theory]
    [InlineData("/etc/passwd")]        // 绝对路径
    [InlineData("../../outside")]      // 相对但解析后落在安装根之外
    public async Task InstallFromArchive_SymlinkEscapingRoot_Rejected(string linkTarget)
    {
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var archive = BuildArchive(work, extra: w => w.WriteEntry(new PaxTarEntry(
                TarEntryType.SymbolicLink, "runtime/evil-link")
            {
                LinkName = linkTarget,
            }));

            var result = await new KinSealSdkInstaller()
                .InstallFromArchiveAsync(archive, Sha256Of(archive), "1.0.0");

            Assert.False(result.Success);
            Assert.Contains("链接", result.Error);
            Assert.False(Directory.Exists(SdkRoot));
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    // ───────────────────────── 随包安装 ─────────────────────────

    [Fact]
    public async Task Bootstrapper_InstallsBundledArchive_ThenShortCircuits()
    {
        var work = TestTempDir.NewDir("sdk");
        var installDir = Path.Combine(work, "app");
        try
        {
            Cleanup();
            var bundled = Path.Combine(installDir, "sdk");
            Directory.CreateDirectory(bundled);
            var archive = BuildArchive(work);
            var target = Path.Combine(bundled, "kinseal-sdk-1.0.0.tar.gz");
            File.Move(archive, target);
            File.WriteAllText(KinSealSdkBootstrapper.ManifestPathFor(target), $$"""
                { "schemaVersion": 1, "version": "1.0.0", "sha256": "{{Sha256Of(target)}}", "sizeBytes": 1 }
                """);

            var first = await KinSealSdkBootstrapper.EnsureAsync(installDir);
            Assert.True(first.Success, first.Error);
            Assert.True(first.Installed);

            var second = await KinSealSdkBootstrapper.EnsureAsync(installDir);
            Assert.True(second.Success);
            Assert.False(second.Installed);   // 已装：不再解压
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    [Fact]
    public async Task Bootstrapper_WithoutManifest_RefusesToInstall()
    {
        var work = TestTempDir.NewDir("sdk");
        var installDir = Path.Combine(work, "app");
        try
        {
            Cleanup();
            var bundled = Path.Combine(installDir, "sdk");
            Directory.CreateDirectory(bundled);
            File.Move(BuildArchive(work), Path.Combine(bundled, "kinseal-sdk-1.0.0.tar.gz"));

            var result = await KinSealSdkBootstrapper.EnsureAsync(installDir);

            Assert.False(result.Success);
            Assert.Contains("清单", result.Error);
            Assert.False(Directory.Exists(SdkRoot));
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }

    [Fact]
    public async Task Bootstrapper_NothingAvailable_ExplainsWhatIsMissing()
    {
        var work = TestTempDir.NewDir("sdk");
        try
        {
            Cleanup();
            var result = await KinSealSdkBootstrapper.EnsureAsync(work);

            Assert.False(result.Success);
            Assert.Contains("未找到", result.Error);
            Assert.False(Directory.Exists(SdkRoot));
        }
        finally
        {
            Cleanup();
            TestTempDir.DeleteDir(work);
        }
    }
}

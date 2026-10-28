using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// `.cgpkg` 打包（37 §13）：64 字节小端头 + ustar 载荷，V1 不压缩/不加密/不签名。
///
/// <para><b>为什么值得判据</b>：包只有板端能读，写歪了在本机毫无征兆 —— 用户拷到 U 盘、
/// 插上板子才看到"工程导入失败"。这里用 BCL 的 <see cref="TarReader"/>（与写入代码
/// 无关的第二套实现）把载荷读回来，逐条与导出目录比对；包头再用测试自己算的 CRC 复核。</para>
/// </summary>
public class ChengePkgWriterTests : IDisposable
{
    private readonly string _dir;

    public ChengePkgWriterTests() => _dir = TestTempDir.NewDir("cgpkg");

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private string ExportFixtureProject()
    {
        var project = ExportFixture.Build();
        // 留样做跨仓库互通时把工程名换成可导入的 id：板端 hmi_import_check 只收
        // 小写字母/数字/下划线（夹具里的中文名会被拒装，那是另一条待办）。
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CHENGE_PKG_KEEP")))
            project.Name = "interop_demo";
        var exportDir = Path.Combine(_dir, "export");
        var result = ExportFixture.Export(project, exportDir);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));
        Assert.True(File.Exists(Path.Combine(exportDir, "manifest.json")), "导出目录里没有 manifest.json");
        return exportDir;
    }

    [Fact]
    public void Pack_WritesHeaderAndUstarPayloadMatchingTheExportedDirectory()
    {
        var exportDir = ExportFixtureProject();
        var pkgPath = exportDir + ".cgpkg";
        var stamp = DateTimeOffset.FromUnixTimeSeconds(1700000000);

        var pkg = ChengePkgWriter.WriteDirectory(exportDir, pkgPath, stamp);
        Assert.True(pkg.Ok, pkg.Error);
        Assert.True(File.Exists(pkgPath));

        var blob = File.ReadAllBytes(pkgPath);

        // ── 包头 ──
        Assert.Equal("HMI1", Encoding.ASCII.GetString(blob, 0, 4));
        Assert.Equal(64, BitConverter.ToUInt16(blob, 4));
        Assert.Equal(1, BitConverter.ToUInt16(blob, 6));
        Assert.Equal(0, BitConverter.ToUInt16(blob, 8));      // format major
        Assert.Equal(6, BitConverter.ToUInt16(blob, 10));     // format minor
        Assert.Equal(0u, BitConverter.ToUInt32(blob, 12));    // flags：不压缩/不加密/不签名
        Assert.Equal(1700000000u, BitConverter.ToUInt32(blob, 24));

        var hw = Encoding.ASCII.GetString(blob, 28, 16).TrimEnd('\0');
        Assert.Equal("f1c100s-kinseal", hw);

        var payload = blob[64..];
        Assert.Equal((int)BitConverter.ToUInt32(blob, 16), payload.Length);
        Assert.Equal(pkg.PayloadBytes, payload.Length);
        Assert.Equal(BitConverter.ToUInt32(blob, 20), Crc32(payload));
        Assert.Equal(BitConverter.ToUInt32(blob, 60), Crc32(blob.AsSpan(0, 60)));

        // ── 载荷：用 BCL 的 TarReader 读回来，逐文件比对 ──
        var packed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var ms = new MemoryStream(payload))
        using (var reader = new TarReader(ms))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.DataStream == null) continue;
                using var s = new MemoryStream();
                entry.DataStream.CopyTo(s);
                packed[entry.Name] = s.ToArray();
            }
        }

        var onDisk = Directory.EnumerateFiles(exportDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(exportDir, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk, packed.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList());
        foreach (var rel in onDisk)
            Assert.Equal(File.ReadAllBytes(Path.Combine(exportDir, rel)), packed[rel]);

        // 跨仓库留样：设了 CHENGE_PKG_KEEP 就把"导出目录 + 包"各留一份，
        // 好让板端解包代码（C）真的读一遍这个包 —— 两个语言写的实现在这里对上，
        // 才算"编辑器导出的包板子能装"。日常单测不设这个变量，什么都不留。
        var keep = Environment.GetEnvironmentVariable("CHENGE_PKG_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            var keepDir = Path.Combine(keep, "project");
            if (Directory.Exists(keepDir)) Directory.Delete(keepDir, true);
            foreach (var rel in onDisk)
            {
                var dst = Path.Combine(keepDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(Path.Combine(exportDir, rel), dst, true);
            }
            Directory.CreateDirectory(keep);
            File.Copy(pkgPath, Path.Combine(keep, "editor.cgpkg"), true);
        }
    }

    /// <summary>
    /// 没有 manifest.json 就不能打包：板端解包后第一件事就是读它，缺了必然被拒。
    /// 宁可在这里红，也不要让用户拿着一个"看起来有 1KB"的包去插 U 盘。
    /// </summary>
    [Fact]
    public void Pack_WithoutManifest_Fails()
    {
        var dir = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(dir);

        var pkg = ChengePkgWriter.WriteDirectory(dir, dir + ".cgpkg", DateTimeOffset.Now);

        Assert.False(pkg.Ok);
        Assert.Contains("manifest.json", pkg.Error);
        Assert.False(File.Exists(dir + ".cgpkg"), "失败时不应该留下半个包");
    }

    /// <summary>
    /// 包头里的 hw_profile 必须等于 manifest 里的那份：板端按它拒装，
    /// 两边不一致就会出现"导出的包本机装不上"。
    /// </summary>
    [Fact]
    public void Pack_HeaderHwProfile_MatchesManifest()
    {
        var exportDir = ExportFixtureProject();
        var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(exportDir, "manifest.json")));
        var expected = manifest.RootElement.GetProperty("hw_profile").GetString();

        var pkg = ChengePkgWriter.WriteDirectory(exportDir, exportDir + ".cgpkg", DateTimeOffset.Now);
        Assert.True(pkg.Ok, pkg.Error);

        var blob = File.ReadAllBytes(exportDir + ".cgpkg");
        var actual = Encoding.ASCII.GetString(blob, 28, 16).TrimEnd('\0');
        Assert.Equal(expected, actual);
    }
}

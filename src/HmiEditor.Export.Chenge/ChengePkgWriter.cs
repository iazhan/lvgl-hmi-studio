using System.Text;
using System.Text.Json;

namespace HmiEditor.Export.Chenge;

/// <summary>CRC-32（IEEE 802.3）。与板端 `chenge-common/src/hmi_crc32.c`、Python `zlib.crc32` 同值。</summary>
internal static class ChengeCrc32
{
    public static uint Compute(ReadOnlySpan<byte> data)
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
}

/// <summary>打包结果：成功时给包路径与载荷字节数，失败给一句能显示的原因。</summary>
public sealed record ChengePkgResult(bool Ok, string? Path, long PayloadBytes, string? Error)
{
    public static ChengePkgResult Fail(string why) => new(false, null, 0, why);
}

/// <summary>
/// 把一个**已导出的 37 工程目录**打成 `.cgpkg`（37 §13）：64 字节小端头 + ustar 载荷，
/// V1 不压缩、不加密、不签名。
///
/// <para><b>为什么从目录读，而不是从导出器的内存映射取</b>：包必须与落在磁盘上的那份
/// 逐字节相同 —— 预览、golden、`--shot` 全都在读那份目录，两份来源早晚会长成两个样。
/// 读目录还能顺手把"导出写了什么"完整体检一遍。</para>
///
/// <para><b>为什么不用 ZipArchive</b>：板端没有 zlib（freetype 静态库明确不带），
/// 而 37 §13 的载荷就是 ustar。这里手写 tar 头是为了与板端解包代码一一对得上，
/// 双方都按同一份文档写；`tools/pkg_selftest.sh` 用第三个实现（Python）做交叉验证。</para>
/// </summary>
public static class ChengePkgWriter
{
    public const int HeaderSize = 64;
    public const int HeaderVersion = 1;
    public const long PayloadMax = 32L * 1024 * 1024;

    private static readonly byte[] Magic = "HMI1"u8.ToArray();

    /// <summary>工程目录 → .cgpkg。失败不写文件（先写 .tmp 再改名，避免半个包）。</summary>
    public static ChengePkgResult WriteDirectory(string projectDir, string outputPath, DateTimeOffset buildTime)
    {
        if (!Directory.Exists(projectDir)) return ChengePkgResult.Fail($"工程目录不存在：{projectDir}");

        var manifestPath = System.IO.Path.Combine(projectDir, "manifest.json");
        if (!File.Exists(manifestPath)) return ChengePkgResult.Fail("工程目录里没有 manifest.json");

        int major, minor;
        string hwProfile;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = doc.RootElement;
            var fv = root.TryGetProperty("format_version", out var fvEl) ? fvEl.GetString() ?? "" : "";
            var parts = fv.Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out major) || !int.TryParse(parts[1], out minor))
                return ChengePkgResult.Fail($"manifest.json 的 format_version 不合法：'{fv}'");
            hwProfile = root.TryGetProperty("hw_profile", out var hw) ? hw.GetString() ?? "" : "";
        }
        catch (JsonException ex)
        {
            return ChengePkgResult.Fail("manifest.json 解析失败：" + ex.Message);
        }

        if (hwProfile.Length == 0) return ChengePkgResult.Fail("manifest.json 缺 hw_profile（板端会拒绝安装）");
        if (Encoding.ASCII.GetByteCount(hwProfile) > 16)
            return ChengePkgResult.Fail($"hw_profile 超过 16 字节：{hwProfile}");

        // 载荷：ustar。条目顺序固定（按相对路径排序），同一份工程每次打出的字节相同。
        using var payload = new MemoryStream();
        var files = Directory.EnumerateFiles(projectDir, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Rel: System.IO.Path.GetRelativePath(projectDir, f).Replace('\\', '/')))
            .OrderBy(x => x.Rel, StringComparer.Ordinal)
            .ToList();

        foreach (var (full, rel) in files)
        {
            var data = File.ReadAllBytes(full);
            var err = WriteTarEntry(payload, rel, data, buildTime);
            if (err != null) return ChengePkgResult.Fail(err);
            if (payload.Length > PayloadMax)
                return ChengePkgResult.Fail($"载荷超过 32MB（{payload.Length} 字节），先精简图片");
        }
        // 归档结束标记：两个全零块
        payload.Write(new byte[1024], 0, 1024);

        var payloadBytes = payload.ToArray();
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        WriteU16(header, 4, HeaderSize);
        WriteU16(header, 6, HeaderVersion);
        WriteU16(header, 8, major);
        WriteU16(header, 10, minor);
        WriteU32(header, 12, 0);                                     // flags：无签名/无加密/无压缩
        WriteU32(header, 16, (uint)payloadBytes.Length);
        WriteU32(header, 20, ChengeCrc32.Compute(payloadBytes));
        WriteU32(header, 24, (uint)buildTime.ToUnixTimeSeconds());
        Encoding.ASCII.GetBytes(hwProfile).CopyTo(header, 28);        // 其余补 0（NUL 补齐）
        WriteU32(header, 60, ChengeCrc32.Compute(header.AsSpan(0, 60)));

        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = outputPath + ".tmp";
            using (var fs = File.Create(tmp))
            {
                fs.Write(header, 0, header.Length);
                fs.Write(payloadBytes, 0, payloadBytes.Length);
            }
            File.Move(tmp, outputPath, overwrite: true);
        }
        catch (IOException ex)
        {
            return ChengePkgResult.Fail("写包失败：" + ex.Message);
        }

        return new ChengePkgResult(true, outputPath, payloadBytes.Length, null);
    }

    /// <summary>写一条 ustar 文件条目；路径过长时按 ustar 的 prefix 字段拆开。返回错误文案或 null。</summary>
    private static string? WriteTarEntry(Stream payload, string relPath, byte[] data, DateTimeOffset mtime)
    {
        var nameBytes = Encoding.UTF8.GetBytes(relPath);
        byte[] prefix = Array.Empty<byte>();
        var name = nameBytes;

        if (nameBytes.Length > 100)
        {
            // ustar：prefix（≤155）+ "/" + name（≤100）
            var slash = relPath.LastIndexOf('/');
            if (slash <= 0) return $"路径过长且没有目录分隔，无法写进 ustar：{relPath}";
            prefix = Encoding.UTF8.GetBytes(relPath[..slash]);
            name = Encoding.UTF8.GetBytes(relPath[(slash + 1)..]);
            if (prefix.Length > 155 || name.Length > 100) return $"路径过长，ustar 装不下：{relPath}";
        }

        var h = new byte[512];
        name.CopyTo(h, 0);
        WriteOctal(h, 100, 8, 0b110_100_100);            // mode 0644
        WriteOctal(h, 108, 8, 0);                        // uid
        WriteOctal(h, 116, 8, 0);                        // gid
        WriteOctal(h, 124, 12, data.Length);             // size
        WriteOctal(h, 136, 12, mtime.ToUnixTimeSeconds());
        h[156] = (byte)'0';                              // typeflag：普通文件
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(h, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(h, 263);
        prefix.CopyTo(h, 345);

        // 校验和：先把 chksum 字段当空格求和，再写 "%06o\0 "
        for (var i = 148; i < 156; i++) h[i] = (byte)' ';
        long sum = 0;
        foreach (var b in h) sum += b;
        WriteOctal(h, 148, 7, sum);
        h[155] = (byte)' ';

        payload.Write(h, 0, h.Length);
        payload.Write(data, 0, data.Length);
        var pad = (512 - data.Length % 512) % 512;
        if (pad > 0) payload.Write(new byte[pad], 0, pad);
        return null;
    }

    private static void WriteOctal(byte[] buf, int offset, int length, long value)
    {
        var s = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        if (s.Length > length - 1) s = s[^(length - 1)..];
        for (var i = 0; i < length - 1; i++) buf[offset + i] = (byte)s[i];
        buf[offset + length - 1] = 0;
    }

    private static void WriteU16(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}

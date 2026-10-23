using HmiEditor.Core.Models;
using SkiaSharp;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 图片资产导出：PNG → RGB565 / RGB565A8 `.bin`（37 §6.15：32 字节头 + 像素 + CRC32）。
///
/// <para>运行时不带 PNG 解码器，`image.src` 只认 .bin；所以"图片没转"在板上表现为
/// 占位方块而不是报错。这里转换失败一律记 warning 并返回 null——控件仍然导出，
/// 但 MAPPING-REPORT 的"未支持"清单里能看到是哪张图。</para>
/// </summary>
internal sealed class ChengeAssets
{
    private readonly AssetLibrary? _assets;
    private readonly string? _projectDir;
    private readonly ChengeIssueBag _issues;
    private readonly Dictionary<string, byte[]> _binaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _srcByAssetId = new(StringComparer.Ordinal);

    public ChengeAssets(AssetLibrary? assets, string? projectDir, ChengeIssueBag issues)
    {
        _assets = assets;
        _projectDir = projectDir;
        _issues = issues;
    }

    /// <summary>相对工程根的图片路径（写入 `props.src`）；失败返回 null。</summary>
    public string? SrcFor(string? assetId, string where)
    {
        if (string.IsNullOrWhiteSpace(assetId)) return null;
        if (_srcByAssetId.TryGetValue(assetId, out var cached)) return cached;

        var item = _assets?.Assets.FirstOrDefault(a => a.Id == assetId);
        if (item == null)
        {
            _issues.Error("asset.missing", where, $"图片资产 {assetId} 不在资源库里");
            return null;
        }

        var local = ResolveLocalPath(item);
        if (local == null || !File.Exists(local))
        {
            _issues.Error("asset.file_missing", where, $"图片文件找不到：{item.FilePath}");
            return null;
        }

        var ext = Path.GetExtension(local).ToLowerInvariant();
        if (ext is not (".png" or ".bmp" or ".jpg" or ".jpeg"))
        {
            _issues.Warn("asset.unsupported_format", where,
                $"图片 {item.Name}{ext} 暂不支持导出（只支持 PNG/BMP/JPEG；GIF/SVG 的运行时替代见 MAPPING-REPORT）");
            return null;
        }

        var name = ChengeNames.Slug(Path.GetFileNameWithoutExtension(item.Name.Length > 0 ? item.Name : local), "img");
        var relative = $"assets/img/{name}.bin";
        if (_binaries.ContainsKey(relative))
            relative = $"assets/img/{name}_{ChengeNames.Slug(assetId, "a")}.bin";

        var bytes = Convert(local, where, item.Name);
        if (bytes == null) return null;

        _binaries[relative] = bytes;
        _srcByAssetId[assetId] = relative;
        return relative;
    }

    public IReadOnlyDictionary<string, byte[]> Binaries => _binaries;

    private string? ResolveLocalPath(AssetItem item)
    {
        if (string.IsNullOrWhiteSpace(item.FilePath)) return null;
        if (Path.IsPathRooted(item.FilePath)) return item.FilePath;
        return string.IsNullOrEmpty(_projectDir) ? item.FilePath : Path.Combine(_projectDir, item.FilePath);
    }

    private byte[]? Convert(string path, string where, string displayName)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                _issues.Warn("asset.decode_failed", where, $"图片 {displayName} 无法解码，已跳过");
                return null;
            }
            if (bitmap.Width > 2048 || bitmap.Height > 2048)
            {
                _issues.Warn("asset.too_large", where, $"图片 {displayName} 单边超过 2048，已跳过");
                return null;
            }

            var w = bitmap.Width;
            var h = bitmap.Height;
            var hasAlpha = false;
            var rgba = new uint[w * h];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    if (c.Alpha < 255) hasAlpha = true;
                    rgba[y * w + x] = c.Alpha == 0
                        ? 0u
                        : ((uint)c.Alpha << 24) | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
                }
            }

            var format = hasAlpha ? 1 : 0;
            var stride = w * 2;
            var colorSize = stride * h;
            var alphaSize = hasAlpha ? w * h : 0;
            var data = new byte[colorSize + alphaSize];

            for (var i = 0; i < w * h; i++)
            {
                var px = rgba[i];
                var r = (px >> 16) & 0xFF;
                var g = (px >> 8) & 0xFF;
                var b = px & 0xFF;
                var rgb565 = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                var o = (i / w) * stride + (i % w) * 2;
                data[o] = (byte)(rgb565 & 0xFF);
                data[o + 1] = (byte)(rgb565 >> 8);
                if (hasAlpha) data[colorSize + i] = (byte)(px >> 24);
            }

            var header = new byte[32];
            WriteAscii(header, 0, "IMG1");
            WriteU16(header, 4, 32);
            WriteU16(header, 6, 1);
            WriteU16(header, 8, (ushort)w);
            WriteU16(header, 10, (ushort)h);
            header[12] = (byte)format;
            header[13] = 0;
            WriteU16(header, 14, 0);
            WriteU32(header, 16, (uint)stride);
            WriteU32(header, 20, (uint)data.Length);
            WriteU32(header, 24, Crc32(data));
            WriteU32(header, 28, 0);

            var result = new byte[header.Length + data.Length];
            Buffer.BlockCopy(header, 0, result, 0, header.Length);
            Buffer.BlockCopy(data, 0, result, header.Length, data.Length);
            return result;
        }
        catch (Exception ex)
        {
            _issues.Warn("asset.decode_failed", where, $"图片 {displayName} 转换失败：{ex.Message}");
            return null;
        }
    }

    private static void WriteAscii(byte[] buf, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++) buf[offset + i] = (byte)text[i];
    }

    private static void WriteU16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteU32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static uint Crc32(byte[] data)
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

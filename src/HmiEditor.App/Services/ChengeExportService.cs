using System.Diagnostics;
using System.Globalization;
using System.Text;
using HmiEditor.App.Resources;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using HmiEditor.Export.Chenge;

namespace HmiEditor.App.Services;

/// <summary>运行时定位结果：找不到就说清"该设哪个东西"，不猜仓库布局。</summary>
public sealed record ChengeRuntimeLocation(string RuntimeDir, string BinaryPath);

public sealed record ChengePreviewResult(
    ChengeExportResult Export,
    string? ScreenshotPng,
    string Log,
    int ExitCode,
    bool TimedOut)
{
    public bool RanOk => Export.Success && ScreenshotPng != null && ExitCode == 0;
}

/// <summary>
/// 导出 + 宿主预览。
///
/// <para><b>为什么预览不是"再写一个模拟器"</b>：宿主 `chenge-runtime` 与板上是同一份渲染代码，
/// 用它截图比在编辑器里重造一套 LVGL 便宜得多，也不会出现"编辑器好看、板上难看"
/// 的两套实现漂移。代价是需要用户先 `make HOST=1` 并把目录告进来（见 <see cref="LocateRuntime"/>）。</para>
/// </summary>
public sealed class ChengeExportService
{
    public const string RuntimeDirSettingKey = "chenge.runtimeDir";
    private const string BinaryName = "chenge-runtime";

    private readonly ChengeExporter _exporter = new();

    public ChengeExportResult Export(HmiProject project, AssetLibrary? assets, string? projectDirectory,
        ChengeExportOptions options, string outputDir)
        => _exporter.Export(project, assets, options, outputDir, projectDirectory);

    /// <summary>导出到内存（预览用：运行时直接吃字节，不写临时目录）。</summary>
    public Dictionary<string, byte[]> ExportToMemory(HmiProject project, AssetLibrary? assets,
        string? projectDirectory, ChengeExportOptions options, out ChengeExportResult result)
        => _exporter.ExportToMemory(project, assets, options, projectDirectory, out result);

    /// <summary>
    /// 定位运行时目录：**只认显式配置**（设置项 / 环境变量 / 安装目录内置），
    /// 不向上遍历也不假设平级仓库 —— 编辑器是独立安装的软件，
    /// 猜路径的后果是"在作者机器上能预览、在用户机器上找不到"。
    /// </summary>
    public static ChengeRuntimeLocation? LocateRuntime(SettingsService? settings)
    {
        var fromSettings = settings?.Chenge.RuntimeDir;
        var fromEnv = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR");
        var fromInstall = Path.Combine(AppContext.BaseDirectory, "chenge-runtime");

        foreach (var dir in new[] { fromSettings, fromEnv, fromInstall })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var binary = Path.Combine(dir, BinaryName);
            if (File.Exists(binary)) return new ChengeRuntimeLocation(dir, binary);
        }
        return null;
    }

    /// <summary>导出到临时目录并跑一次宿主运行时截图（无显示，SDL dummy）。</summary>
    public async Task<ChengePreviewResult> PreviewAsync(
        HmiProject project,
        AssetLibrary? assets,
        string? projectDirectory,
        ChengeExportOptions options,
        SettingsService? settings,
        string? clickSequence,
        int timeoutSeconds = 25)
    {
        var workRoot = AppPaths.TempSubdir("chenge-preview");
        var exportDir = Path.Combine(workRoot, "project");
        if (Directory.Exists(exportDir)) Directory.Delete(exportDir, recursive: true);
        Directory.CreateDirectory(exportDir);

        var export = Export(project, assets, projectDirectory, options, exportDir);
        if (!export.Success)
            return new ChengePreviewResult(export, null, DescribeIssues(export), -1, false);

        var runtime = LocateRuntime(settings);
        if (runtime == null)
        {
            return new ChengePreviewResult(export, null,
                AppLocalization.Service["chenge.export.runtime_missing"],
                -1, false);
        }

        var shotBmp = Path.Combine(workRoot, "shot.bmp");
        if (File.Exists(shotBmp)) File.Delete(shotBmp);
        var dataDir = Path.Combine(workRoot, "data");
        Directory.CreateDirectory(dataDir);

        var psi = new ProcessStartInfo(runtime.BinaryPath)
        {
            WorkingDirectory = runtime.RuntimeDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["SDL_VIDEODRIVER"] = "dummy";
        psi.Environment["HMI_PROJECT_DIR"] = exportDir;
        psi.Environment["HMI_DATA_DIR"] = dataDir;
        psi.Environment["HMI_SHOT"] = shotBmp;
        psi.Environment["HMI_RT_TRACE"] = "1";
        if (!string.IsNullOrWhiteSpace(clickSequence))
            psi.Environment["HMI_HOST_CLICK"] = clickSequence.Trim();

        var log = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };

        var timedOut = false;
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            }
        }
        catch (Exception ex)
        {
            lock (log) log.AppendLine(AppLocalization.Service["chenge.preview.launch_failed"] + ex.Message);
        }

        var png = File.Exists(shotBmp) ? TryConvertBmp(shotBmp, Path.Combine(workRoot, "shot.png")) : null;
        return new ChengePreviewResult(export, png, log.ToString(), timedOut ? -1 : process.ExitCode, timedOut);
    }

    public static string DescribeIssues(ChengeExportResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.Summary);
        foreach (var issue in result.Issues.Items.Where(i => i.Severity == ChengeIssueSeverity.Error))
            sb.AppendLine("ERROR " + issue.Code + " @ " + issue.Location + "：" + ExternalText.Describe(issue));
        foreach (var issue in result.Issues.Items.Where(i => i.Severity == ChengeIssueSeverity.Warning))
            sb.AppendLine("WARN  " + issue.Code + " @ " + issue.Location + "：" + ExternalText.Describe(issue));
        return sb.ToString();
    }

    /// <summary>
    /// SDL_SaveBMP 产物 → PNG。走自己的解码而不是 Skia 编解码器：Skia **不含 BMP 解码器**，
    /// 直接喂给 Avalonia Bitmap 会得到"预览一片空白"这种最难查的失败。
    /// </summary>
    private static string? TryConvertBmp(string bmpPath, string pngPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(bmpPath);
            if (bytes.Length < 54 || bytes[0] != 'B' || bytes[1] != 'M') return null;
            var dataOffset = BitConverter.ToInt32(bytes, 10);
            var headerSize = BitConverter.ToInt32(bytes, 14);
            var width = BitConverter.ToInt32(bytes, 18);
            var height = BitConverter.ToInt32(bytes, 22);
            var bpp = BitConverter.ToInt16(bytes, 28);
            var compression = BitConverter.ToInt32(bytes, 30);
            if (compression != 0 || (bpp != 24 && bpp != 32)) return null;

            var bottomUp = height > 0;
            var h = Math.Abs(height);
            var bytesPerPixel = bpp / 8;
            var stride = ((width * bytesPerPixel + 3) / 4) * 4;

            using var bitmap = new SkiaSharp.SKBitmap(width, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
            var pixels = bitmap.GetPixelSpan();
            for (var y = 0; y < h; y++)
            {
                var srcRow = dataOffset + (bottomUp ? (h - 1 - y) : y) * stride;
                for (var x = 0; x < width; x++)
                {
                    var src = srcRow + x * bytesPerPixel;
                    if (src + 2 >= bytes.Length) break;
                    var dst = y * bitmap.RowBytes + x * 4;
                    pixels[dst] = bytes[src];
                    pixels[dst + 1] = bytes[src + 1];
                    pixels[dst + 2] = bytes[src + 2];
                    pixels[dst + 3] = bytesPerPixel == 4 ? bytes[src + 3] : (byte)255;
                }
            }

            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(pngPath, data.ToArray());
            return pngPath;
        }
        catch
        {
            return null;
        }
    }
}

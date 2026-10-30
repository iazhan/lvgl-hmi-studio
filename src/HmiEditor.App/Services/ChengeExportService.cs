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

    /// <summary>截图预览用的运行时可执行文件名（Linux 无后缀，Windows 带 <c>.exe</c>）。</summary>
    public static string BinaryName => OperatingSystem.IsWindows() ? "chenge-runtime.exe" : "chenge-runtime";

    /// <summary>
    /// 隔离预览宿主的文件名（设计画布 / 实时预览都跑它）。
    /// Windows 包只随带这一个可执行文件（它不需要 SDL 窗口），截图预览也退回用它。
    /// </summary>
    public static string HostBinaryName
        => OperatingSystem.IsWindows() ? "chenge-preview-host.exe" : "chenge-preview-host";

    private readonly ChengeExporter _exporter = new();

    public ChengeExportResult Export(HmiProject project, AssetLibrary? assets, string? projectDirectory,
        ChengeExportOptions options, string outputDir)
        => _exporter.Export(project, assets, options, outputDir, projectDirectory);

    /// <summary>导出到内存（预览用：运行时直接吃字节，不写临时目录）。</summary>
    public Dictionary<string, byte[]> ExportToMemory(HmiProject project, AssetLibrary? assets,
        string? projectDirectory, ChengeExportOptions options, out ChengeExportResult result)
        => _exporter.ExportToMemory(project, assets, options, projectDirectory, out result);

    /// <summary>
    /// 只导出元数据到内存（设计画布增量重载用）：manifest/tags/devices/ports/strings/
    /// alarms/savecfg/users，不含页面与资产。
    /// </summary>
    public Dictionary<string, byte[]> ExportMetaOnly(HmiProject project, AssetLibrary? assets,
        string? projectDirectory, ChengeExportOptions options, ChengePageStats pageStats,
        out ChengeExportResult result)
        => _exporter.ExportMetaOnly(project, assets, options, projectDirectory, pageStats, out result);

    /// <summary>只导出某一页的页面 JSON（设计画布推当前页/受影响页用）。</summary>
    public byte[]? ExportPageOnly(HmiProject project, AssetLibrary? assets, string? projectDirectory,
        ChengeExportOptions options, string screenId, out ChengeExportResult result,
        out Dictionary<string, string> widgetIds)
        => _exporter.ExportPageOnly(project, assets, options, projectDirectory, screenId, out result, out widgetIds);

    /// <summary>
    /// 把一个**已导出**的 37 工程目录打成 `.cgpkg`（37 §13）。U 盘里放这个文件即可，
    /// 板端「维护 → 更新组态」会解包并切为活动工程。
    ///
    /// <para>导出目录仍然要留：预览、golden、`--shot` 读的都是目录；包只是分发形态。</para>
    /// </summary>
    public ChengePkgResult ExportPackage(string exportedDir, string packagePath)
        => ChengePkgWriter.WriteDirectory(exportedDir, packagePath, DateTimeOffset.Now);

    /// <summary>
    /// 定位运行时目录：**只认显式配置**（设置项 / 环境变量 / 安装目录内置），
    /// 不向上遍历也不假设平级仓库 —— 编辑器是独立安装的软件，
    /// 猜路径的后果是"在作者机器上能预览、在用户机器上找不到"。
    ///
    /// <para><b>安装包形态</b>：随包分发的运行时放在
    /// <c>&lt;安装目录&gt;/chenge-runtime/chenge-runtime[.exe]</c>，这条候选在设置项与环境变量
    /// 之后，所以"装完即用"与"临时指到别处调试"两件事互不打扰。绿色版把二进制直接丢在
    /// 安装目录下也算（也认 <c>&lt;安装目录&gt;/chenge-runtime[.exe]</c>）。</para>
    /// <para><paramref name="baseDir"/> 只为判据可注入，默认取编辑器所在目录。</para>
    /// </summary>
    public static ChengeRuntimeLocation? LocateRuntime(SettingsService? settings, string? baseDir = null)
    {
        foreach (var dir in RuntimeSearchDirs(settings, baseDir))
        {
            // 优先截图用的进程版；没有就退回预览宿主 —— Windows 包只带宿主一个可执行文件
            var binary = Path.Combine(dir, BinaryName);
            if (File.Exists(binary)) return new ChengeRuntimeLocation(dir, binary);

            var host = Path.Combine(dir, HostBinaryName);
            if (File.Exists(host)) return new ChengeRuntimeLocation(dir, host);
        }
        return null;
    }

    /// <summary>
    /// 找运行时**实际会看**的目录顺序（设置项 → 环境变量 → 安装目录内置 → 安装目录本身）。
    /// 找不到时把这份清单原样给用户看：装完提示"没有运行时"却不说找过哪里，只能靠猜。
    /// </summary>
    public static IReadOnlyList<string> RuntimeSearchDirs(SettingsService? settings, string? baseDir = null)
    {
        var root = baseDir ?? AppContext.BaseDirectory;
        var list = new List<string>();

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!list.Contains(dir, StringComparer.Ordinal)) list.Add(dir);
        }

        Add(settings?.Chenge.RuntimeDir);
        Add(Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR"));
        Add(Path.Combine(root, "chenge-runtime"));
        Add(root);
        return list;
    }

    /// <summary>
    /// "找不到运行时"的完整文案：原有提示 + **实际找过的目录**。
    /// 安装包一旦漏带运行时，用户看到的必须是"我找过这些地方"，而不是干瞪眼。
    /// </summary>
    public static string RuntimeMissingMessage(SettingsService? settings)
    {
        var text = new StringBuilder(AppLocalization.Service["chenge.export.runtime_missing"]);
        var dirs = RuntimeSearchDirs(settings);

        if (dirs.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(AppLocalization.Service["chenge.export.runtime_missing.tried"]);
            foreach (var dir in dirs) text.Append("· ").AppendLine(dir);
        }
        return text.ToString().TrimEnd();
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
            return new ChengePreviewResult(export, null, RuntimeMissingMessage(settings), -1, false);
        }

        var log = new StringBuilder();
        var shotBmp = Path.Combine(workRoot, "shot.bmp");
        if (File.Exists(shotBmp)) File.Delete(shotBmp);
        var dataDir = Path.Combine(workRoot, "data");
        Directory.CreateDirectory(dataDir);

        // 两种运行时入口：进程版吃环境变量；预览宿主吃命令行参数（且不需要 SDL）。
        // Windows 安装包里只有宿主那一个 exe（它不链 SDL），所以这里必须两条都能走。
        var isHost = runtime.BinaryPath.EndsWith(HostBinaryName, StringComparison.Ordinal);
        var psi = new ProcessStartInfo(runtime.BinaryPath)
        {
            WorkingDirectory = runtime.RuntimeDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isHost)
        {
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(exportDir);
            psi.ArgumentList.Add("--data");
            psi.ArgumentList.Add(dataDir);
            psi.ArgumentList.Add("--shm");
            psi.ArgumentList.Add("/chenge-shot-" + Guid.NewGuid().ToString("N")[..12]);
            psi.ArgumentList.Add("--shot");
            psi.ArgumentList.Add(shotBmp);
            psi.ArgumentList.Add("--frames");
            psi.ArgumentList.Add("3");
            // 宿主版的自动点击（HMI_HOST_CLICK 那套）还没接：宁可明确说"没点"，
            // 也不要让调用方以为点过了（导出 e2e 判据靠这个结论）。
            if (!string.IsNullOrWhiteSpace(clickSequence))
                log.AppendLine("[preview] 当前运行时入口是预览宿主，暂不支持自动点击注入，已忽略 clickSequence");
        }
        else
        {
            psi.Environment["SDL_VIDEODRIVER"] = "dummy";
            psi.Environment["HMI_PROJECT_DIR"] = exportDir;
            psi.Environment["HMI_DATA_DIR"] = dataDir;
            psi.Environment["HMI_SHOT"] = shotBmp;
            psi.Environment["HMI_RT_TRACE"] = "1";
            if (!string.IsNullOrWhiteSpace(clickSequence))
                psi.Environment["HMI_HOST_CLICK"] = clickSequence.Trim();
        }

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

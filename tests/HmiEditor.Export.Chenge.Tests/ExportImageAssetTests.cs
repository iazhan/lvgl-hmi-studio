using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using HmiEditor.Core.Models;
using Xunit;

namespace HmiEditor.Export.Chenge.Tests;

/// <summary>
/// 图片资产的两条判据：`props.src` 只写文件名，以及带图片的页面在宿主运行时里真的能建起来。
///
/// <para><b>为什么"图片能显示"不够</b>：运行时按 <c>&lt;工程根&gt;/assets/img/&lt;src&gt;</c> 拼路径
/// （<c>ui_image_bin.c</c> 的 <c>image_bin_path</c>）。src 里多带一层 <c>assets/img/</c> 时文件打不开，
/// <c>build_image</c> 返回 -1，而 <c>ui_page</c> 对单个控件建失败是**整页失败** ——
/// 现象是"带图的那一页打不开"，不是"图不显示"。所以第二条判据看的是有没有截图。</para>
/// </summary>
public class ExportImageAssetTests : IDisposable
{
    /// <summary>1×1 PNG。手写常量而不是现场编码：这条判据不该依赖 SkiaSharp 的编码器可用。</summary>
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly string _dir;

    public ExportImageAssetTests() => _dir = TestTempDir.NewDir("image-asset");

    public void Dispose() => TestTempDir.DeleteDir(_dir);

    /// <summary>
    /// 控件图与页面背景图都只写 <c>logo.bin</c>：运行时自己拼 <c>assets/img/</c>。
    /// 顺带钉住 .bin 落在 <c>assets/img/</c> 下且头是 IMG1。
    /// </summary>
    [Fact]
    public void ImageWidget_SrcIsFileNameOnly_AndBinIsWritten()
    {
        var (project, assets) = BuildProject();
        var result = Export(project, assets);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var page = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "pages", "home.json"))).RootElement;
        var image = page.GetProperty("widgets").EnumerateArray()
            .First(w => w.GetProperty("type").GetString() == "image");
        var src = image.GetProperty("props").GetProperty("src").GetString();

        Assert.Equal("logo.bin", src);
        Assert.DoesNotContain("/", src);

        var bg = page.GetProperty("bg_image").GetProperty("src").GetString();
        Assert.Equal("logo.bin", bg);

        var bin = Path.Combine(_dir, "assets", "img", "logo.bin");
        Assert.True(File.Exists(bin), "assets/img/logo.bin 没写出来");
        var bytes = File.ReadAllBytes(bin);
        Assert.Equal("IMG1", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    /// <summary>
    /// 带图片的页面必须能建起来（有截图）。src 多带一层目录时这条会红 —— 那时现象是
    /// 整页失败，而不是图片变占位块。
    /// </summary>
    [Fact]
    public void ImagePage_OpensInRuntime()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("CHENGE_RUNTIME_DIR") ?? "";
        var binary = runtimeDir.Length == 0 ? "" : Path.Combine(runtimeDir, "chenge-runtime");
        if (binary.Length == 0 || !File.Exists(binary)) return;   // 没宿主二进制：跳过（与同目录渲染判据一致）

        var (project, assets) = BuildProject();
        var result = Export(project, assets);
        Assert.True(result.Success, string.Join("\n", result.Issues.Items));

        var shot = Path.Combine(_dir, "image.bmp");
        var psi = new ProcessStartInfo(binary)
        {
            WorkingDirectory = runtimeDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SDL_VIDEODRIVER"] = "dummy";
        psi.Environment["HMI_PROJECT_DIR"] = _dir;
        psi.Environment["HMI_DATA_DIR"] = Path.Combine(_dir, "data");
        psi.Environment["HMI_SHOT"] = shot;
        psi.Environment["HMI_RT_TRACE"] = "1";

        using var process = Process.Start(psi)!;
        var log = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(20000), "运行时 20s 未退出");
        Assert.True(File.Exists(shot),
            $"带图片的页面没建起来（src 多带一层 assets/img/ 时就是这个现象）\n{log}");
    }

    // ── 夹具 ──────────────────────────────────────────────

    private (HmiProject Project, AssetLibrary Assets) BuildProject()
    {
        var png = Path.Combine(_dir, "src.png");
        File.WriteAllBytes(png, Convert.FromBase64String(OnePixelPng));

        var assets = new AssetLibrary();
        assets.AddAsset(new AssetItem { Id = "a1", Name = "logo", Type = AssetType.Image, FilePath = png });

        var project = ExportFixture.Build();
        project.Screens[0].Controls.Clear();
        project.Screens[0].Controls.Add(new HmiControl
        {
            Id = "c_img", Name = "Logo", Type = HmiControlType.Image,
            X = 20, Y = 20, Width = 40, Height = 40,
            ImageAssetId = "a1",
        });
        project.Screens[0].BgImageAssetId = "a1";
        return (project, assets);
    }

    private ChengeExportResult Export(HmiProject project, AssetLibrary assets) =>
        new ChengeExporter().Export(project, assets, new ChengeExportOptions(), _dir, null);
}

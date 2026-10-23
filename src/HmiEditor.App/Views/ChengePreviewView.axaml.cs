using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using HmiEditor.App.Controls;
using HmiEditor.App.Resources;
using HmiEditor.App.Services;
using HmiEditor.Core.Models;
using HmiEditor.Export.Chenge;

namespace HmiEditor.App.Views;

/// <summary>
/// 导出 + 宿主运行时预览：左边是宿主截的图，右边是运行时日志。
/// 日志是主要证据（建页成功 = `page: &lt;id&gt; 打开`，失败会写原因），截图只用来核对排版。
/// </summary>
public partial class ChengePreviewView : ChromeWindow
{
    private readonly ChengeExportService _service;
    private readonly HmiProject _project;
    private readonly AssetLibrary? _assets;
    private readonly string? _projectDir;
    private readonly ChengeExportOptions _options;
    private readonly SettingsService? _settings;
    private ChengePreviewResult _preview;

    public ChengePreviewView()
    {
        InitializeComponent();
        _service = null!;
        _project = null!;
        _preview = null!;
        BtnClose.Click += (_, _) => Close();
        BtnTitleClose.Click += (_, _) => Close();
    }

    public ChengePreviewView(ChengeExportService service, HmiProject project, AssetLibrary? assets,
        string? projectDir, ChengeExportOptions options, SettingsService? settings,
        ChengePreviewResult preview, string? initialClicks) : this()
    {
        _service = service;
        _project = project;
        _assets = assets;
        _projectDir = projectDir;
        _options = options;
        _settings = settings;
        _preview = preview;
        ClicksBox.Text = initialClicks ?? "";
        BtnRerun.Click += async (_, _) => await RerunAsync();
        BtnOpenDir.Click += (_, _) => OpenOutputDir();
        Render(preview);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnTitleCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private async Task RerunAsync()
    {
        BtnRerun.IsEnabled = false;
        SummaryText.Text = AppLocalization.Service["chenge.preview.running"];
        try
        {
            _preview = await _service.PreviewAsync(_project, _assets, _projectDir, _options, _settings,
                string.IsNullOrWhiteSpace(ClicksBox.Text) ? null : ClicksBox.Text);
            Render(_preview);
        }
        finally
        {
            BtnRerun.IsEnabled = true;
        }
    }

    private void Render(ChengePreviewResult preview)
    {
        SummaryText.Text = preview.Export.Summary +
            AppLocalization.Service[preview.RanOk ? "chenge.preview.ok_suffix" : "chenge.preview.fail_suffix"];
        LogBox.Text = preview.Log;

        if (preview.ScreenshotPng != null && File.Exists(preview.ScreenshotPng))
        {
            ShotImage.Source = new Bitmap(preview.ScreenshotPng);
            ShotPlaceholder.IsVisible = false;
        }
        else
        {
            ShotImage.Source = null;
            ShotPlaceholder.IsVisible = true;
            ShotPlaceholder.Text = AppLocalization.Service["chenge.preview.no_shot"];
        }
    }

    private void OpenOutputDir()
    {
        var dir = _preview.Export.OutputDirectory;
        if (!Directory.Exists(dir)) return;
        try
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // 无桌面环境时打不开文件管理器——日志里已有路径，不算失败
        }
    }
}

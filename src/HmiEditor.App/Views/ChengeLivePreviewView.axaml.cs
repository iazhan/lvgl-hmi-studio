using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HmiEditor.App.Controls;
using HmiEditor.App.Resources;
using HmiEditor.App.Services;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using HmiEditor.Export.Chenge;

namespace HmiEditor.App.Views;

/// <summary>
/// 可操作的实时预览：把运行时当库加载（<see cref="ChengeEmbedSession"/>），
/// 帧贴到 Avalonia 位图上，鼠标事件换算成指针注回运行时。
///
/// <para><b>它和"导出并预览"的分工</b>：那个是"导出目录 + 起进程 + 截一张图"，
/// 用来验收产物与看日志；这个用来在编辑器里真的点一下、拖一下。
/// 两者读的是同一份导出器产物，所以"预览能点"与"板上能点"是同一套控件代码。</para>
///
/// <para><b>画布仍是 C#</b>：这里只显示运行时出的位图，不做任何控件级重绘；
/// 选择框/手柄仍在设计画布上（Avalonia 自绘），两者互不影响。</para>
/// </summary>
public partial class ChengeLivePreviewView : ChromeWindow
{
    private readonly HmiProject _project;
    private readonly AssetLibrary? _assets;
    private readonly string? _projectDirectory;
    private readonly ChengeExportOptions _options;
    private readonly SettingsService? _settings;

    private ChengeEmbedSession? _session;
    private WriteableBitmap? _bitmap;
    private byte[] _frame = Array.Empty<byte>();
    private DispatcherTimer? _timer;
    private long _lastVersion = -1;
    private string _exportDir = "";
    private bool _busy;

    /// <summary>设计器/无参构造：只为了让 XAML 预览器能加载。</summary>
    public ChengeLivePreviewView()
    {
        InitializeComponent();
        _project = null!;
    }

    public ChengeLivePreviewView(HmiProject project, AssetLibrary? assets, string? projectDirectory,
        ChengeExportOptions options, SettingsService? settings)
    {
        InitializeComponent();
        _project = project;
        _assets = assets;
        _projectDirectory = projectDirectory;
        _options = options;
        _settings = settings;
        Placeholder.Text = AppLocalization.Service["live.starting"];
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = StartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        _session?.Dispose();
        _session = null;
        base.OnClosed(e);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnTitleCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private async Task StartAsync()
    {
        if (_busy) return;
        _busy = true;
        BtnReload.IsEnabled = false;
        try
        {
            _timer?.Stop();
            _session?.Dispose();
            _session = null;
            ErrorText.Text = "";

            var runtime = ChengeExportService.LocateRuntime(_settings);
            if (runtime == null)
            {
                Fail(AppLocalization.Service["chenge.export.runtime_missing"]);
                return;
            }
            // 进程内预览要的是库，不是可执行文件
            if (!ChengeEmbedSession.TryLocate(runtime.RuntimeDir, out var libPath))
            {
                Fail(AppLocalization.Service[Lang.LiveLibMissing]);
                return;
            }

            // 1) 导出到临时目录（与"导出"走同一个导出器，产物一致）
            var workRoot = AppPaths.TempSubdir("chenge-live");
            _exportDir = Path.Combine(workRoot, "project");
            if (Directory.Exists(_exportDir)) Directory.Delete(_exportDir, recursive: true);
            Directory.CreateDirectory(_exportDir);

            var export = await Task.Run(() =>
                new ChengeExportService().Export(_project, _assets, _projectDirectory, _options, _exportDir));
            if (!export.Success)
            {
                Fail(ChengeExportService.DescribeIssues(export));
                return;
            }

            var dataDir = Path.Combine(workRoot, "data");
            Directory.CreateDirectory(dataDir);

            // 2) 加载库并打开工程
            _session = await Task.Run(() =>
            {
                var s = new ChengeEmbedSession(libPath, dataDir);
                if (s.Open(_exportDir)) return s;
                var why = s.LastError;
                s.Dispose();
                throw new InvalidOperationException(why);
            });

            // 3) 位图（LVGL 的 ARGB8888 在内存里是 BGRA）
            _bitmap = new WriteableBitmap(new PixelSize(_session.Width, _session.Height),
                new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _frame = new byte[_session.Stride * _session.Height];
            ScreenImage.Source = _bitmap;
            ScreenImage.Width = _session.Width;
            ScreenImage.Height = _session.Height;
            Placeholder.IsVisible = false;
            BtnShot.IsEnabled = true;

            StateText.Text = AppLocalization.Service.Format("live.state",
                _session.Width, _session.Height, _exportDir);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (_, _) => Pump();
            _timer.Start();
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            _busy = false;
            BtnReload.IsEnabled = true;
        }
    }

    /// <summary>UI 线程：有新帧就拷进 WriteableBitmap 并通知重绘。</summary>
    private void Pump()
    {
        if (_session == null || _bitmap == null) return;

        var version = _session.FrameVersion;
        if (version == _lastVersion) return;
        _lastVersion = version;

        if (!_session.TryCopyFrame(_frame)) return;
        using (var fb = _bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(_frame, 0, fb.Address, _frame.Length);
        }
        ScreenImage.InvalidateVisual();

        FpsText.Text = AppLocalization.Service.Format("live.fps", _session.Fps.ToString("0.0"));
    }

    private async void OnReloadClicked(object? sender, RoutedEventArgs e)
    {
        _lastVersion = -1;
        await StartAsync();
    }

    private void OnShotClicked(object? sender, RoutedEventArgs e)
    {
        if (_session == null) return;
        var path = Path.Combine(_exportDir, "shot.bmp");
        if (_session.SaveScreenshot(path))
            FootText.Text = AppLocalization.Service.Format("live.shot.saved", path);
        else
            FootText.Text = AppLocalization.Service["live.shot.failed"];
    }

    // ── 指针：屏幕坐标 = (鼠标 - 居中偏移) / 缩放 ──

    private (int X, int Y)? MapPoint(Point p)
    {
        if (_session == null) return null;
        var b = ScreenImage.Bounds;
        var scale = Math.Min(b.Width / _session.Width, b.Height / _session.Height);
        if (scale <= 0) return null;
        var shownW = _session.Width * scale;
        var shownH = _session.Height * scale;
        var dx = (b.Width - shownW) / 2;
        var dy = (b.Height - shownH) / 2;
        var x = (int)((p.X - dx) / scale);
        var y = (int)((p.Y - dy) / scale);
        if (x < 0 || y < 0 || x >= _session.Width || y >= _session.Height) return null;
        return (x, y);
    }

    private void OnScreenPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (MapPoint(e.GetPosition(ScreenImage)) is not { } pt) return;
        e.Pointer.Capture(ScreenImage);
        _session?.Pointer(pt.X, pt.Y, true);
        e.Handled = true;
    }

    private void OnScreenPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(ScreenImage).Properties.IsLeftButtonPressed) return;
        if (MapPoint(e.GetPosition(ScreenImage)) is not { } pt) return;
        _session?.Pointer(pt.X, pt.Y, true);
    }

    private void OnScreenPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (MapPoint(e.GetPosition(ScreenImage)) is { } pt)
            _session?.Pointer(pt.X, pt.Y, false);
        e.Pointer.Capture(null);
    }

    private void Fail(string message)
    {
        _timer?.Stop();
        ErrorText.Text = message;
        Placeholder.IsVisible = true;
        Placeholder.Text = message;
        StateText.Text = "";
    }
}

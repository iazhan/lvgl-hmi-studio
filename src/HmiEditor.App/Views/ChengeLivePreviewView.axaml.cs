using System;
using System.IO;
using System.Linq;
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

    private IChengePreviewSession? _session;
    private string _backend = "";
    private string _warnings = "";
    private WriteableBitmap? _bitmap;
    private byte[] _frame = Array.Empty<byte>();
    private DispatcherTimer? _timer;
    private long _lastVersion = -1;
    private string _exportDir = "";
    private bool _busy;
    private readonly List<string> _watchNames = new();
    private long _tagMark;
    private long _renderCount;
    private long _pumpCount;      /* 诊断：出帧泵每秒跑了几拍 */
    private long _verCount;       /* 诊断：宿主每秒推进了几帧 */
    private long _uiTicks;
    private long _fpsWarnedMs;    /* 帧率告警的节流时刻（每 5s 最多一行） */
    private long _fpsMark = Environment.TickCount64;

    // ── 心跳（见 Services/PreviewHealth.cs）──
    private long _lastFrameChangeMs;     // 最近一次 seq 推进的时刻
    private long _heartbeatMark;         // 上次检查的时刻（500ms 节流）
    private long _firstCrashMs;          // 本轮第一次崩溃的时刻（0 = 还没崩过）
    private int _autoRestarts;
    private bool _restarting;
    private string _frozenLog = "";      // 崩溃前的宿主日志：dispose 后就读不到了

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
                Fail(ChengeExportService.RuntimeMissingMessage(_settings));
                return;
            }

            var workRoot = AppPaths.TempSubdir("chenge-live");
            _exportDir = Path.Combine(workRoot, "project");
            var dataDir = Path.Combine(workRoot, "data");
            Directory.CreateDirectory(dataDir);

            var service = new ChengeExportService();
            string? isolatedWhy = null;

            // 先导出到内存：隔离与进程内两条后端都从这一份产物起步，
            // 不再"隔离就得先写临时目录"。
            var (export, blobs) = await Task.Run(() =>
            {
                var mem = service.ExportToMemory(_project, _assets, _projectDirectory, _options, out var res);
                return (res, mem);
            });
            if (!export.Success)
            {
                Fail(ChengeExportService.DescribeIssues(export));
                return;
            }
            _warnings = export.Issues.WarningCount > 0
                ? ChengeExportService.DescribeIssues(export)
                : "";

            // 后端 1（默认）：隔离进程，工程从内存推过去（宿主 --project-stdin）。
            if (ChengeProcessSession.TryLocate(runtime.RuntimeDir, out var hostExe))
            {
                var proc = new ChengeProcessSession(hostExe, dataDir);
                if (await Task.Run(() => proc.OpenWithBlob(blobs)))
                {
                    _session = proc;
                    _backend = AppLocalization.Service["live.backend.isolated"];
                }
                else
                {
                    // 含图片资产时内存表还没覆盖 assets/img/*.bin ⇒ 回落目录再试一次
                    isolatedWhy = proc.LastError;
                    proc.Dispose();

                    var onDisk = await Task.Run(() =>
                        service.Export(_project, _assets, _projectDirectory, _options, _exportDir));
                    if (onDisk.Success)
                    {
                        var proc2 = new ChengeProcessSession(hostExe, dataDir);
                        if (await Task.Run(() => proc2.Open(_exportDir)))
                        {
                            _session = proc2;
                            _backend = AppLocalization.Service["live.backend.isolated"];
                        }
                        else
                        {
                            isolatedWhy += " / " + proc2.LastError;
                            proc2.Dispose();
                        }
                    }
                }
            }

            // 后端 2：进程内库（同一份内存工程）。隔离宿主没编出来/起不来时才走。
            if (_session == null)
            {
                if (!ChengeEmbedSession.TryLocate(runtime.RuntimeDir, out var libPath))
                {
                    Fail(AppLocalization.Service[Lang.LiveLibMissing] +
                         (isolatedWhy == null ? "" : "\n" + isolatedWhy));
                    return;
                }

                _session = await Task.Run(() =>
                {
                    var s = new ChengeEmbedSession(libPath, dataDir);
                    s.ClearProjectFiles();
                    foreach (var (rel, bytes) in blobs) s.PutFile(rel, bytes);
                    if (s.OpenMemory()) return s;

                    var memWhy = s.LastError;
                    if (Directory.Exists(_exportDir)) Directory.Delete(_exportDir, recursive: true);
                    var onDisk = service.Export(_project, _assets, _projectDirectory, _options, _exportDir);
                    if (!onDisk.Success)
                    {
                        s.Dispose();
                        throw new InvalidOperationException(ChengeExportService.DescribeIssues(onDisk));
                    }
                    if (s.Open(_exportDir)) return s;

                    var why = AppLocalization.Service.Format("live.open.failed", memWhy, s.LastError);
                    s.Dispose();
                    throw new InvalidOperationException(why);
                });
                _backend = AppLocalization.Service["live.backend.inproc"];
            }

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
                _session.Width, _session.Height, _backend);

            // 心跳从"第一帧到达"开始计时：建页/首帧之前的等待由 WaitSharedMemory 的超时管，
            // 这里若从打开瞬间计时，慢机器上第一帧还没出来就会被误判成卡死。
            _lastFrameChangeMs = Environment.TickCount64;
            _heartbeatMark = _lastFrameChangeMs;
            _frozenLog = "";

            CollectWatchNames();
            // 优先级用 Normal 而不是 Render：这个 Tick 自己会 InvalidateVisual（排出一次渲染），
            // 挂在 Render 优先级上等于和"它自己排出来的渲染"抢同一个优先级档 —— 连续出帧时
            // 会被挤到后面，表现是"管道自己在动时帧率极低（2~6），点一下画面反而快了"
            // （点击是输入事件，顺带把积压的 Tick 一起放行）。出帧泵必须在渲染之前跑。
            _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
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

        var nowMs = Environment.TickCount64;
        _pumpCount++;                       // 诊断：出帧泵自己跑了多少拍（与"有没有新帧"无关）
        var version = _session.FrameVersion;
        if (version == _lastVersion)
        {
            CheckHealth(nowMs);
            ReportFps(nowMs);               // 没有新帧也要每秒报一次，否则读数会冻在旧值
            return;
        }
        _verCount++;                        // 诊断：宿主真的推进了多少帧
        _lastVersion = version;
        _lastFrameChangeMs = nowMs;

        // 量"贴图 + 失效"这一段：帧率上不去时先看是这段贵，还是 tick 线程给不出帧。
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_session.TryCopyFrame(_frame))
        {
            ReportFps(nowMs);
            return;
        }
        using (var fb = _bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(_frame, 0, fb.Address, _frame.Length);
        }
        ScreenImage.InvalidateVisual();
        _uiTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

        // 显示**实际刷新的帧率**，不是 tick 频率：tick 一直 60 而画面不动时，
        // 这个数字会掉到 0，正好区分"没渲染"与"渲染了但内容没变"。
        _renderCount++;
        ReportFps(nowMs);
    }

    /// <summary>
    /// 每秒一次把读数刷到界面与日志。
    /// <para>三个数分工：<c>pump</c> = 出帧泵跑了几拍（定时器有没有被饿死）、
    /// <c>ver</c> = 宿主推进了几帧（有没有东西在动）、<c>paint</c> = 真正贴图+重绘了几次。
    /// 三者一比就知道瓶颈在哪：pump 低=UI 线程忙；ver 低=画面本来就静止；
    /// ver 高而 paint 低=贴图/重绘跟不上。</para>
    /// </summary>
    private void ReportFps(long nowMs)
    {
        if (nowMs - _fpsMark < 1000) return;

        var span = nowMs - _fpsMark;
        var fps = _renderCount * 1000.0 / span;
        var ver = _verCount * 1000.0 / span;
        var pump = _pumpCount * 1000.0 / span;
        var uiMs = _renderCount == 0
            ? 0
            : _uiTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _renderCount;
        FpsText.Text = AppLocalization.Service.Format("live.fps",
            fps.ToString("0.0"), _session!.Fps.ToString("0"), uiMs.ToString("0.0"));

        // 帧率偏低时把四个数打进日志（每 5s 最多一行，够定位又不刷屏）。
        if (fps < 25 && nowMs - _fpsWarnedMs >= 5000)
        {
            _fpsWarnedMs = nowMs;
            Console.WriteLine($"[live] 帧率偏低 paint={fps:0.0}/s ver={ver:0.0}/s " +
                              $"pump={pump:0.0}/s ui={uiMs:0.0}ms backend={_backend}");
        }

        _renderCount = 0;
        _verCount = 0;
        _pumpCount = 0;
        _uiTicks = 0;
        _fpsMark = nowMs;

        // 宿主日志：隔离模式下这是唯一能看到运行时说法的窗口
        // （比如 "keypad: 文本键盘" / "input: 键盘打不开"）。
        var log = _session!.Log;
        var tail = log.Count == 0
            ? ""
            : string.Join("\n", log.Count <= 24 ? log : log.Skip(log.Count - 24));
        var text = string.IsNullOrEmpty(_warnings) ? tail
            : string.IsNullOrEmpty(tail) ? _warnings : _warnings + "\n---\n" + tail;
        if (text != WatchBox.Text)
        {
            WatchBox.Text = text;
            WatchBox.CaretIndex = text.Length;
        }
        PumpTags(nowMs);
    }

    /// <summary>
    /// 变量监视的取样名单：当前工程里**被控件绑定的**变量（按首次出现顺序、去重）。
    /// 不列全量变量表——一页几十个变量刷起来没意义，用户要看的是"我点这个控件改了谁"。
    /// </summary>
    private void CollectWatchNames()
    {
        _watchNames.Clear();
        if (_project == null) return;

        var byId = _project.VariableManager.Variables
            .GroupBy(v => v.Id)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in _project.Screens)
        foreach (var control in screen.Controls)
        foreach (var binding in control.VariableBindings)
        {
            if (byId.TryGetValue(binding.VariableId, out var name) && seen.Add(name))
                _watchNames.Add(name);
        }
    }

    private void PumpTags(long nowMs)
    {
        if (_session == null) return;
        if (nowMs - _tagMark < 400) return;
        _tagMark = nowMs;

        var sb = new System.Text.StringBuilder();
        foreach (var name in _watchNames.Take(24))
        {
            _session.PollTag(name);
            sb.Append(name).Append(" = ")
              .Append(_session.TryGetTag(name, out var v) ? v : "…").Append('\n');
        }
        var text = sb.ToString();
        if (text != TagBox.Text) TagBox.Text = text;
    }

    /// <summary>
    /// 心跳：每 500ms 看一次"帧序号还在不在动"。只有隔离模式有进程可判死；
    /// 进程内模式崩的是编辑器自己，谈不上重启。
    /// </summary>
    private void CheckHealth(long nowMs)
    {
        if (_session is not ChengeProcessSession || _restarting) return;
        if (nowMs - _heartbeatMark < 500) return;
        _heartbeatMark = nowMs;

        if (!PreviewHealth.IsStalled(_session.IsOpen, _lastFrameChangeMs, nowMs)) return;

        var why = _session.IsOpen ? "" : _session.LastError;
        _ = HandleCrashAsync(why);
    }

    /// <summary>
    /// 宿主没了/卡死了：冻结日志 → 停表 → 释放 → 在窗口内自动重启，超限就交给人。
    ///
    /// <para><b>为什么要冻结日志</b>：日志是从会话对象里读的，<c>Dispose</c> 之后就读不到了；
    /// 而崩溃现场恰恰是唯一能说明"为什么崩"的东西。所以先拷最后十几行再释放。</para>
    /// </summary>
    private async Task HandleCrashAsync(string why)
    {
        if (_restarting) return;
        _restarting = true;
        try
        {
            if (_session is ChengeProcessSession proc)
            {
                var log = proc.Log;
                _frozenLog = string.Join("\n", log.Count <= 12 ? log : log.Skip(log.Count - 12));
            }

            _timer?.Stop();
            _timer = null;
            _session?.Dispose();
            _session = null;

            var now = Environment.TickCount64;
            if (_firstCrashMs == 0) _firstCrashMs = now;

            if (PreviewHealth.ShouldAutoRestart(_autoRestarts, _firstCrashMs, now))
            {
                _autoRestarts++;
                StateText.Text = AppLocalization.Service.Format("live.crashed",
                    _autoRestarts.ToString(), PreviewHealth.MaxAutoRestarts.ToString());
                _lastVersion = -1;
                await StartAsync();
                return;
            }

            Fail(AppLocalization.Service.Format(Lang.LiveCrashedGiveup, _autoRestarts.ToString()) +
                 (string.IsNullOrEmpty(why) ? "" : "\n" + why));
        }
        finally
        {
            _restarting = false;
        }
    }

    private async void OnReloadClicked(object? sender, RoutedEventArgs e)
    {
        // 手动重来：把崩溃计数清零，否则一次卡死之后永远只剩"放弃"
        _autoRestarts = 0;
        _firstCrashMs = 0;
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
    //
    // ⚠ 抬起**必须无条件发出去**。早先的实现在坐标越界时直接 return，
    // 于是"按下 → 拖出画面 → 松开"会让运行时一直停在 PRESSED：
    // 之后所有点击都被当成"按住没放"，表现就是键盘不弹、滑块不跟手。
    // 所以拖动与抬起一律钳位到屏幕内，只有"按下"那一下要求落在画面里。

    private bool _pointerDown;

    private (int X, int Y)? MapPoint(Point p, bool clamp)
    {
        if (_session == null || _session.Width <= 0 || _session.Height <= 0) return null;
        var b = ScreenImage.Bounds;
        var scale = Math.Min(b.Width / _session.Width, b.Height / _session.Height);
        if (scale <= 0) return null;
        var dx = (b.Width - _session.Width * scale) / 2;
        var dy = (b.Height - _session.Height * scale) / 2;
        var x = (int)Math.Round((p.X - dx) / scale);
        var y = (int)Math.Round((p.Y - dy) / scale);
        if (clamp)
        {
            x = Math.Clamp(x, 0, _session.Width - 1);
            y = Math.Clamp(y, 0, _session.Height - 1);
            return (x, y);
        }
        if (x < 0 || y < 0 || x >= _session.Width || y >= _session.Height) return null;
        return (x, y);
    }

    private void ShowPointer(int x, int y, bool down)
    {
        FootText.Text = $"ptr=({x},{y}) {(down ? "down" : "up")}";
    }

    private void OnScreenPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (MapPoint(e.GetPosition(ScreenImage), clamp: false) is not { } pt) return;
        _pointerDown = true;
        e.Pointer.Capture(ScreenImage);
        _session?.Pointer(pt.X, pt.Y, true);
        ShowPointer(pt.X, pt.Y, true);
        e.Handled = true;
    }

    private void OnScreenPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown) return;
        if (MapPoint(e.GetPosition(ScreenImage), clamp: true) is not { } pt) return;
        _session?.Pointer(pt.X, pt.Y, true);
        ShowPointer(pt.X, pt.Y, true);
    }

    private void OnScreenPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        if (MapPoint(e.GetPosition(ScreenImage), clamp: true) is { } pt)
        {
            _session?.Pointer(pt.X, pt.Y, false);
            ShowPointer(pt.X, pt.Y, false);
        }
        else
        {
            _session?.Pointer(0, 0, false);
        }
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>捕获丢失（窗口失焦/被抢走）：补一次抬起，别把运行时留在按下态。</summary>
    private void OnScreenPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_pointerDown) return;
        _pointerDown = false;
        _session?.Pointer(0, 0, false);
        FootText.Text = "ptr lost → up";
    }

    private void Fail(string message)
    {
        _timer?.Stop();
        ErrorText.Text = message;
        Placeholder.IsVisible = true;
        Placeholder.Text = message;
        StateText.Text = "";

        // 崩溃现场：日志区留在屏幕上，别让用户只剩一句"预览已停止"
        if (!string.IsNullOrEmpty(_frozenLog))
        {
            var text = AppLocalization.Service["live.frozenlog"] + "\n" + _frozenLog;
            WatchBox.Text = text;
            WatchBox.CaretIndex = text.Length;
        }
    }
}

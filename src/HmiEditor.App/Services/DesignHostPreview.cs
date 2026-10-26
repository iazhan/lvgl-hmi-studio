using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using HmiEditor.App.Controls;
using HmiEditor.App.Resources;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using HmiEditor.Export.Chenge;

namespace HmiEditor.App.Services;

/// <summary>
/// 设计画布的「真机帧源」：导出当前工程 → 起 <c>--design</c> 宿主 → 把帧泵到
/// <see cref="DesignCanvas.HostFrame"/>。
///
/// <para><b>为什么放在子进程</b>：设计画布一旦显示真机画面，任何改属性的操作都会驱动
/// 宿主重绘；宿主里的 Lua/原生崩溃不能把编辑器带走（与实时预览同一条理由）。</para>
///
/// <para><b>与实时预览的分工</b>：那边推进时间、可点击操作；这边用 <c>--design</c>
/// 不推进时间，只出**静止的真图** —— 报警条不滚、曲线不采样，同一份工程每次渲染的
/// 像素必须一样，才能与宿主 <c>--shot</c> 逐像素对拍。</para>
///
/// <para><b>改动后怎么刷新</b>：S1 只有整页重载（<c>L</c>）。属性级补丁与拖动跟随是 S2/S3
/// 的代码；在此之前拖动控件时位图是冻结的，用户看到的是叠加层。</para>
/// </summary>
public sealed class DesignHostPreview : IDisposable
{
    private ChengeProcessSession? _session;
    private DesignCanvas? _canvas;
    private WriteableBitmap? _bitmap;
    private byte[] _frame = Array.Empty<byte>();
    private DispatcherTimer? _timer;
    private long _lastVersion = -1;
    private uint _epoch;
    private bool _disposed;

    /// <summary>编辑器控件 Id → 导出后的 widget id（补丁按后者寻址）。</summary>
    private Dictionary<string, string> _widgetIds = new(StringComparer.Ordinal);

    /// <summary>待发送的几何：widget id → 最新矩形。同一控件拖 16ms 内的多次变化只留最后一条。</summary>
    private readonly Dictionary<string, (int X, int Y, int W, int H)> _pending =
        new(StringComparer.Ordinal);

    private long _lastSendMs;
    private long _lastChangeMs;
    private long _changeStamp;
    private uint _patchSeq;
    private bool _needsReload;
    private bool _reloading;

    // 停手后要重新导出页面 JSON：宿主里那份是启动时的快照，不含拖动后的新坐标
    private HmiProject? _project;
    private AssetLibrary? _assets;
    private string? _projectDirectory;
    private ChengeExportOptions? _options;
    private string _pageId = "";

    public bool IsRunning => _session?.IsOpen == true;

    public string LastError { get; private set; } = "";

    /// <summary>导出并起宿主；失败时 <see cref="LastError"/> 给原因，画布保持原样。</summary>
    public async Task<bool> StartAsync(HmiProject project, AssetLibrary? assets, string? projectDirectory,
        ChengeExportOptions options, SettingsService? settings, string? currentScreenId, DesignCanvas canvas)
    {
        Stop();
        _canvas = canvas;
        _lastVersion = -1;
        _epoch = 0;

        var runtime = ChengeExportService.LocateRuntime(settings);
        if (runtime == null || !ChengeProcessSession.TryLocate(runtime.RuntimeDir, out var hostExe))
        {
            LastError = AppLocalization.Service["canvas.host.err.no_host"];
            return false;
        }

        var workRoot = AppPaths.TempSubdir("chenge-design");
        var dataDir = Path.Combine(workRoot, "data");
        Directory.CreateDirectory(dataDir);

        var service = new ChengeExportService();
        var (export, blobs) = await Task.Run(() =>
        {
            var mem = service.ExportToMemory(project, assets, projectDirectory, options, out var res);
            return (Result: res, Files: mem);
        });
        if (!export.Success)
        {
            LastError = ChengeExportService.DescribeIssues(export);
            return false;
        }
        _widgetIds = new Dictionary<string, string>(export.WidgetIds, StringComparer.Ordinal);

        // 停手后要重导出页面 JSON（宿主里那份是此刻的快照）
        _project = project;
        _assets = assets;
        _projectDirectory = projectDirectory;
        _options = options;
        // 当前屏导出成了哪个页 id：只有导出器知道（PageIds），不能按字母序猜 ——
        // 猜错的话切屏后推给宿主的是别的页 JSON，画面会是另一页的内容。
        _pageId = options.StartPageId ?? "";
        if (string.IsNullOrEmpty(_pageId) && !string.IsNullOrEmpty(currentScreenId)
            && export.PageIds.TryGetValue(currentScreenId, out var mappedPageId))
            _pageId = mappedPageId;
        if (string.IsNullOrEmpty(_pageId))
        {
            var key = blobs.Keys.Where(k => k.StartsWith("pages/", StringComparison.Ordinal))
                        .OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (!string.IsNullOrEmpty(key)) _pageId = Path.GetFileNameWithoutExtension(key);
        }

        Console.WriteLine($"[design] start screen={currentScreenId ?? "<null>"} " +
                          $"startScreenId={options.StartScreenId ?? "<null>"} " +
                          $"pageIds={string.Join("|", export.PageIds.Select(kv => kv.Key + "=" + kv.Value))} " +
                          $"page={_pageId}");

        var proc = new ChengeProcessSession(hostExe, dataDir, designMode: true);
        if (!await Task.Run(() => proc.OpenWithBlob(blobs)))
        {
            LastError = proc.LastError;
            proc.Dispose();
            return false;
        }

        if (proc.Width <= 0 || proc.Height <= 0)
        {
            LastError = AppLocalization.Service["canvas.host.err.bad_screen"];
            proc.Dispose();
            return false;
        }

        _session = proc;
        _frame = new byte[proc.Stride * proc.Height];
        _bitmap = new WriteableBitmap(new PixelSize(proc.Width, proc.Height),
            new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        canvas.HostFrame = _bitmap;
        canvas.UseHostFrame = true;

        // 建立页面代：宿主用 applied_epoch 丢弃"旧页面的补丁"
        _epoch = 1;
        proc.ReloadPage(_epoch, null);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
        Pump();
        return true;
    }

    /// <summary>UI 线程一拍：先补丁（可能让宿主重绘），再取帧。</summary>
    private void Pump()
    {
        if (_session == null || _bitmap == null || _canvas == null) return;

        FlushPatches();
        PullFrame();
    }

    /// <summary>把积压的几何补丁发出去；静置一会儿再整页重载一次。</summary>
    private void FlushPatches()
    {
        if (_session == null) return;
        var now = Environment.TickCount64;

        if (_pending.Count > 0)
        {
            if (now - _lastSendMs < 16) return;   // 拖动再快也只按 60Hz 发
            _lastSendMs = now;
            foreach (var (wid, r) in _pending)
            {
                _patchSeq++;
                _session.PatchRect(_epoch, _patchSeq, wid, r.X, r.Y, r.W, r.H);
            }
            // 一行一批：定位"拖动时改到了哪个控件"要靠它（不打就只剩猜）
            var first = _pending.First();
            Console.WriteLine($"[design] W epoch={_epoch} n={_pending.Count} " +
                              $"first={first.Key} {first.Value.X},{first.Value.Y} {first.Value.W}x{first.Value.H}");
            _pending.Clear();
            return;
        }

        // 几何快路径只改 lv_obj 的位置尺寸，不重算控件内部布局（仪表刻度、曲线留白、
        // 表格列宽都是建页时算的）。停手后重导出页面 JSON 再整页重建，画面才精确。
        // 注意必须是**重导出**：宿主里的快照不含拖动后的新坐标，直接 L 会把控件拉回原位。
        if (_needsReload && !_reloading && now - _lastChangeMs >= 300)
        {
            _reloading = true;
            _ = ReloadWithFreshJsonAsync();
        }
    }

    /// <summary>重新导出工程、取当前页 JSON、推给宿主重建（后台线程导出，UI 线程发送）。</summary>
    private async Task ReloadWithFreshJsonAsync()
    {
        var stamp = _changeStamp;   // 这一轮用哪个时刻的模型
        try
        {
            var project = _project;
            var assets = _assets;
            var dir = _projectDirectory;
            var options = _options;
            if (project == null || options == null || _session == null) return;

            var (export, files) = await Task.Run(() =>
            {
                var mem = new ChengeExportService().ExportToMemory(project, assets, dir, options, out var res);
                return (Result: res, Files: mem);
            });
            if (!export.Success || _session == null) return;
            if (!files.TryGetValue($"pages/{_pageId}.json", out var bytes)) return;

            // 映射必须跟着这次导出的结果走：新建的控件在上一份快照里不存在，
            // 不更新的话它在宿主里永远寻不到址 —— 表现是"新建后拖动不跟手，
            // 切屏重启一次（重新导出）才好"。
            _widgetIds = new Dictionary<string, string>(export.WidgetIds, StringComparer.Ordinal);

            _epoch++;
            _session.ReloadPageJson(_epoch, bytes);
            Console.WriteLine($"[design] J epoch={_epoch} bytes={bytes.Length} stamp={stamp}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Console.WriteLine($"[design] J 失败：{ex.Message}");
        }
        finally
        {
            _reloading = false;
            // 只有"这一轮用的模型仍然是最新的"才算刷完；期间又改过就留着标志，
            // 下一拍（16ms 后）会用新模型再来一轮 —— 否则那次改动要等用户再动一下才生效。
            if (_changeStamp == stamp) _needsReload = false;
        }
    }

    private void PullFrame()
    {
        if (_session == null || _bitmap == null || _canvas == null) return;

        var version = _session.FrameVersion;
        if (version == _lastVersion) return;
        if (!_session.TryCopyFrame(_frame)) return;

        using (var fb = _bitmap.Lock())
            Marshal.Copy(_frame, 0, fb.Address, Math.Min(_frame.Length, fb.RowBytes * _bitmap.PixelSize.Height));

        _lastVersion = version;
        // HostFrame 引用没变，StyledProperty 不会触发 AffectsRender —— 手动点一次
        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// 画布上某个控件变了（拖动时每次移动都会来一条）。只发几何；属性改动会在
    /// 停手 300ms 后由整页重载生效 —— 重建单个控件会丢事件（见 ui_page.h 的说明）。
    /// </summary>
    public void OnControlChanged(HmiControl control)
    {
        if (!IsRunning || control == null || string.IsNullOrEmpty(control.Id)) return;
        if (!_widgetIds.TryGetValue(control.Id, out var wid)) return;

        _pending[wid] = ((int)Math.Round(control.X), (int)Math.Round(control.Y),
                         (int)Math.Round(control.Width), (int)Math.Round(control.Height));
        _lastChangeMs = Environment.TickCount64;
        _changeStamp++;
        _needsReload = true;
    }

    /// <summary>
    /// 结构变化（新增/删除控件）：几何补丁无法表达"多了一个控件"，必须整页重建。
    /// 走与停手后刷新同一条路（重导出 → J），所以新控件会带着当前模型值出现。
    /// </summary>
    public void NotifyStructureChanged()
    {
        if (!IsRunning) return;
        _changeStamp++;
        // 结构变化不能等"停手 300ms"：新建的控件在宿主里还不存在，映射也要这次导出才补齐。
        // 把时间戳拨到已过阈值，下一拍就重建，拖动跟手只差这一轮（几十毫秒）。
        _lastChangeMs = Environment.TickCount64 - 400;
        _needsReload = true;
        Console.WriteLine("[design] 结构变化 → 立即整页重建");
    }

    /// <summary>让宿主重绘一帧（不推进时间）。属性补丁接上之前，改完属性用它。</summary>
    public bool RequestRedraw() => _session?.RenderOnce() ?? false;

    /// <summary>整页重载（增删控件/改绑定/换页之后）。代价是整页重建，S1 用它保证不漂。</summary>
    public bool ReloadPage()
    {
        if (_session == null) return false;
        _epoch++;
        return _session.ReloadPage(_epoch, null);
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;

        if (_canvas != null)
        {
            // 先回退帧源再释放位图：否则下一次 Render 会读到已 Dispose 的位图
            _canvas.UseHostFrame = false;
            _canvas.HostFrame = null;
        }

        _session?.Dispose();
        _session = null;
        _bitmap = null;
        _frame = Array.Empty<byte>();
        _lastVersion = -1;
        _pending.Clear();
        _widgetIds.Clear();
        _needsReload = false;
        _reloading = false;
        _patchSeq = 0;
        _project = null;
        _options = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

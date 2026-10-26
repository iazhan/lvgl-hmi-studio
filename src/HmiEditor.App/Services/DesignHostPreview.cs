using System;
using System.Collections.Generic;
using System.IO;
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

    public bool IsRunning => _session?.IsOpen == true;

    public string LastError { get; private set; } = "";

    /// <summary>导出并起宿主；失败时 <see cref="LastError"/> 给原因，画布保持原样。</summary>
    public async Task<bool> StartAsync(HmiProject project, AssetLibrary? assets, string? projectDirectory,
        ChengeExportOptions options, SettingsService? settings, DesignCanvas canvas)
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

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
        Pump();
        return true;
    }

    /// <summary>UI 线程：有新帧才拷进位图并让画布重绘（没有就什么都不做）。</summary>
    private void Pump()
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
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

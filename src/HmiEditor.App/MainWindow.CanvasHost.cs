using System;
using Avalonia.Controls;
using HmiEditor.App.Resources;
using HmiEditor.App.Services;
using HmiEditor.App.Views;

namespace HmiEditor.App;

/// <summary>
/// 设计画布「真机渲染」开关（独立分部：MainWindow.axaml.cs 有行数基线）。
///
/// 打开时把底图换成宿主渲染出的真机位图，关掉回 C# 自绘。切换期间画布上的
/// 选中框/手柄/网格都不变 —— 它们本来就画在叠加层、位置取模型。
/// </summary>
public partial class MainWindow
{
    private DesignHostPreview? _designHost;

    /// <summary>
    /// 工程/屏幕就绪后自动挂上真机渲染（默认帧源）。
    ///
    /// <para>失败不打断：没编宿主、没有工程、导出失败都只写状态栏，画布回退 C# 自绘。
    /// 刚 clone 的环境本来就没有 chenge-preview-host，"打开工程就报错"不能接受。</para>
    /// </summary>
    /// <summary>
    /// 是否在工程就绪时自动挂真机渲染（默认 true）。测试置 false：那里没有宿主进程、
    /// 也不该起进程，画布回退 C# 自绘即可 —— 判据测的是编辑器逻辑，不是渲染来源。
    /// </summary>
    public static bool AutoStartDesignHost { get; set; } = true;

    private string? _designHostScreenId;

    /// <summary>
    /// 正在起宿主（StartAsync 里有导出 + 起进程两次 await，几十毫秒到几秒）。
    /// 期间再来的启动请求只记下"想要哪一屏"，等这一轮结束补一次 —— 并发起两个宿主会让
    /// **后完成的那个拿到画布**（<c>canvas.HostFrame</c> 后写覆盖），却被先完成的那个占住
    /// <c>_designHost</c>：画面拿到的是一个没人刷新的位图，表现就是"刚打开什么都没有，
    /// 点两次真机渲染开关才出图"。
    /// </summary>
    private bool _hostStarting;

    /// <summary>启动期间被要求的屏幕（完成后再对齐一次）。</summary>
    private string? _pendingHostScreenId;

    /// <summary>属性面板几何框上次刷新的时刻（拖动中节流到 100ms 一次）。</summary>
    private long _lastGeometryRefreshMs;

    /// <summary>
    /// 工程/屏幕切换后对齐真机画面。宿主有自己的页面栈，切屏后它仍停在旧页 ——
    /// 位图会一直显示上一屏。现阶段整会话重启是最省的正确解；下一步改成发 G 切页 +
    /// 推该页 JSON，免掉这 1–2 秒。
    /// </summary>
    private void SyncDesignHostScreen()
    {
        if (!AutoStartDesignHost) return;
        ReapOrphanHosts();

        var screenId = _currentScreen?.Id;
        if (_designHost?.IsRunning == true &&
            string.Equals(screenId, _designHostScreenId, StringComparison.Ordinal))
            return;

        EnsureDesignHostAsync(screenId);
    }

    private static bool _orphansReaped;

    /// <summary>
    /// 清一次上一轮被强杀留下的宿主（ppid=1）：它们攥着共享内存与 LVGL 缓冲，新会话
    /// 抢不到资源时的表现就是"画布一片空白，点两次真机渲染开关才出图"。
    /// 放在这里而不是 MainWindow 构造函数：那条文件有行数基线。
    /// </summary>
    private static void ReapOrphanHosts()
    {
        if (_orphansReaped) return;
        _orphansReaped = true;

        var n = Services.ChengeProcessSession.ReapOrphans();
        if (n > 0) Console.WriteLine($"[design] reaped {n} orphan host(s)");
    }

    /// <summary>
    /// 起/重启宿主，**串行**：正在起就只登记目标屏，结束后补一次。
    /// 手动开关（<see cref="ToggleCanvasHostFrame"/>）也走这里，两条入口不会各起一个。
    /// </summary>
    private async void EnsureDesignHostAsync(string? screenId, int attempt = 0)
    {
        if (_hostStarting)
        {
            _pendingHostScreenId = screenId;
            return;
        }

        _hostStarting = true;
        var failed = false;
        try
        {
            if (_designHost != null)
            {
                if (screenId != _designHostScreenId)
                    EditorLog.Line($"[design] 切屏 {_designHostScreenId} → {screenId}：重启宿主");
                _designHost.Dispose();          /* Dispose 内部先 Stop：帧源回退再释放位图 */
                _designHost = null;
            }
            _designHostScreenId = null;

            if (_currentProject == null) return;

            var canvasView = GetActiveView<DesignCanvasView>("Canvas") ?? CanvasPanel;
            var host = new DesignHostPreview();
            var ok = await host.StartAsync(_currentProject, _assetLibrary, _projectDirectory,
                BuildChengeExportOptions(), _settingsService, screenId, canvasView.Canvas);
            if (!ok)
            {
                var why = host.LastError;
                var tail = host.HostLogTail;
                EditorLog.Line($"[design] host start failed (attempt {attempt}) screen={screenId}: " +
                               $"{why} | hostLog={tail}");
                host.Dispose();
                failed = true;
                StatusText.Text = why;
                return;
            }

            _designHost = host;
            _designHostScreenId = screenId;
            EditorLog.Line($"[design] host ready screen={screenId} " +
                           $"epoch={_designHost.GetHashCode():x8} firstFrame={host.HasFirstFrame}");
            StatusText.Text = AppLocalization.Service["canvas.host.on"];
        }
        catch (Exception ex)
        {
            failed = true;
            ReportOperationFailure(Lang.UiOpExportCode, ex);
        }
        finally
        {
            _hostStarting = false;
            var want = _pendingHostScreenId;
            _pendingHostScreenId = null;

            // 起失败就自己再试一次：宿主首帧慢（字体/渲染冷启动）时，用户此前的唯一出路
            // 是把「设计画布用真机渲染」点了又点 —— 那不该由人来补。
            if (failed && _designHost == null && attempt == 0)
                EnsureDesignHostAsync(screenId, 1);
            else if (want != null && !string.Equals(want, _designHostScreenId, StringComparison.Ordinal))
                EnsureDesignHostAsync(want);
        }
    }

    private async void ToggleCanvasHostFrame()
    {
        try
        {
            if (_designHost?.IsRunning == true)
            {
                _designHost.Stop();
                StatusText.Text = AppLocalization.Service["canvas.host.off"];
                return;
            }

            if (_currentProject == null)
            {
                StatusText.Text = LocService["chenge.export.no_project"];
                return;
            }

            EnsureDesignHostAsync(_currentScreen?.Id);
        }
        catch (Exception ex)
        {
            ReportOperationFailure(Lang.UiOpExportCode, ex);
        }
    }
}

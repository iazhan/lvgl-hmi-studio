using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HmiEditor.App.Resources;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;

namespace HmiEditor.App;

/// <summary>
/// 新建工程的入口与它专用的两个小工具。
///
/// <para><b>为什么单独成文件</b>：`MainWindow.axaml.cs` 有精确行数基线（只许降）。
/// 新建流程要问名字/位置/型号、按型号定分辨率、建完立刻落盘 —— 这些只服务这一条路径，
/// 留在主文件里只会把基线顶上去。</para>
/// </summary>
public partial class MainWindow
{
    private async void NewProject()
    {
        try { await NewProjectCore(); }
        catch (Exception ex) { ReportOperationFailure(Lang.UiOpNewProject, ex); }
    }

    private async Task NewProjectCore()
    {
        // 如果有未保存的更改，提示用户保存、放弃或取消
        if (_hasUnsavedChanges)
        {
            var confirm = new NewProjectConfirmDialog
            {
                Message = LocService.Format("confirm.discard.changes", _currentProject.Name)
            };
            await confirm.ShowDialog(this);
            if (!confirm.Confirmed)
            {
                StatusText.Text = LocService["status.project.cancelled"];
                return;
            }
        }

        // 名字/存储位置/型号在建工程前一次问清：名字同时决定默认文件名与板上 id 推导，
        // 型号决定分辨率与内存预算 —— 建完再改要连带改文件名，代价大得多。
        // 预选当前工程的型号：连着建几个同类工程时不必每次重选。
        var dialog = new Views.NewProjectView(DefaultNewProjectLocation(), _currentProject.HardwareTargetId);
        await dialog.ShowDialog(this);
        if (dialog.Result is not { } request)
        {
            StatusText.Text = LocService["status.project.cancelled"];
            return;
        }

        _currentProject = new HmiProject
        {
            Name = request.Name,
            LvglVersion = "9.5",
            HardwareTargetId = request.HardwareTargetId
        };
        ApplyTargetDisplayDefaults(_currentProject);
        // 应用工程安全配置到认证服务（会话超时/密码策略/锁定策略）
        _authService.ApplySecurityConfig(_currentProject.SecurityConfig);
        _currentScreen = ScreenFactory.Create(_currentProject, name: "Screen 1");
        _currentProject.Screens.Add(_currentScreen);

        // 新工程建立资源工作目录与空白资产库
        _assetLibrary = new AssetLibrary();
        _assetDirectory = CreateWorkspaceDirectory(_currentProject);

        // 新工程还没有 .hmi：目录引用等落盘成功后再由 SaveToPath 接管
        _projectDirectory = null;
        _currentHmiPath = null;
        RefreshGitState();

        // 重新绑定模板管理器到新项目，并注入预置模板
        InitializeTemplateManager();
        RebindTemplateManager();

        // 重置所有面板到新项目/屏幕
        // 先重新绑定项目浏览器到新项目（切换项目必须 LoadProject，否则其内部 _project
        // 仍指向旧工程对象，导致树显示旧内容 —— 历史遗留 Bug 修复）
        ProjectExplorerPanel.LoadProject(_currentProject, _currentScreen);
        LoadProjectIntoPanels();
        SetUnsavedChanges(false);
        Title = LocService["app.title"] + $" — {_currentProject.Name}";
        UpdateStatusBar();

        // 名字与目录是刚填的：立刻落盘，否则关掉编辑器这一份就没了（也才会进「最近打开」）
        await SaveToPath(request.FilePath, Path.GetFileName(request.FilePath));
        StatusText.Text = LocService["status.project.created"];
    }

    /// <summary>
    /// 新建工程的默认存储目录：优先用「最近打开工程」所在目录（同类工程通常放在一起），
    /// 否则用系统「文档」目录 —— 不把工程默认塞进安装目录或临时目录。
    /// </summary>
    private string DefaultNewProjectLocation()
    {
        var recent = _settingsService.RecentProjects.List.FirstOrDefault();
        var dir = string.IsNullOrWhiteSpace(recent) ? null : Path.GetDirectoryName(recent);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrEmpty(documents) || !Directory.Exists(documents)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : documents;
    }

    /// <summary>
    /// 新建工程时把型号的默认分辨率写进工程默认值 —— 不写就会落到 800×480 兜底，
    /// 换一个型号后屏幕上摆好的控件与实机像素对不上。
    /// </summary>
    private void ApplyTargetDisplayDefaults(HmiProject project)
    {
        var target = ResolveCurrentHardwareTarget();
        if (target == null) return;
        project.ScreenDefaultsConfig.DefaultWidth = target.Display.DefaultWidth;
        project.ScreenDefaultsConfig.DefaultHeight = target.Display.DefaultHeight;
    }
}

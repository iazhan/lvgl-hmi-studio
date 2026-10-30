using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Hmi.Hardware.Builtin;
using Hmi.Hardware.Contracts;
using Hmi.Hardware.Loader;
using HmiEditor.App.Controls;
using HmiEditor.App.Resources;

namespace HmiEditor.App.Views;

/// <summary>
/// 「新建工程」对话框的返回值 —— 只描述**用户填了什么**，不碰工程对象：
/// 工程实例由调用方（`MainWindow.NewProjectCore`）按这里的三个值建，避免对话框持有整棵工程树。
/// </summary>
public sealed record NewProjectRequest(string Name, string HardwareTargetId, string FilePath);

/// <summary>
/// 新建工程的**信息收集**对话框：工程名 / 存储位置 / 目标型号，并在落盘前把型号的分辨率、
/// 色深、内存预算摊给用户看（型号选错在这儿就能发现，而不是导出后到板子上才发现）。
///
/// <para>工程名同时决定默认文件名（`&lt;名称&gt;.hmi`）与「板上工程 id」的推导 —— 后者留空时按名字生成，
/// 所以这里不许出现空名；清空会被拒绝而不是悄悄存成 Untitled。</para>
/// </summary>
public partial class NewProjectView : ChromeWindow
{
    private readonly List<IHardwareTarget> _targets = new();

    /// <summary>用户点了「创建」才有值；取消/关窗为 null。</summary>
    public NewProjectRequest? Result { get; private set; }

    /// <summary>仅用于 XAML 设计器。</summary>
    public NewProjectView()
    {
        InitializeComponent();
    }

    public NewProjectView(string defaultLocation, string? defaultTargetId = null) : this()
    {
        InitializeTargets(defaultTargetId);
        TxtLocation.Text = defaultLocation;

        CmbTarget.SelectionChanged += (_, _) => UpdateSummary();
        BtnBrowse.Click += OnBrowseClicked;
        BtnCancel.Click += (_, _) => Close();
        BtnCreate.Click += OnCreateClicked;

        UpdateSummary();
        Opened += (_, _) =>
        {
            TxtProjectName.Focus();
            TxtProjectName.SelectAll();
        };
    }

    /// <summary>
    /// 型号清单：`firmware/*/firmware.json` 生成的固件型号 + 内置 kinseal（无 JSON，契约版本由 C# 类声明）。
    /// 与工程设置、资源仪表盘走同一条加载路径，避免三处各列一份。
    /// </summary>
    private void InitializeTargets(string? defaultTargetId)
    {
        var registry = new HardwareTargetRegistry();
        registry.LoadFirmwareTargets(Path.Combine(AppContext.BaseDirectory, "firmware"));
        registry.Register(new AllwinnerF1C100STarget());

        _targets.Clear();
        _targets.AddRange(registry.GetAllTargets());

        var items = _targets
            .OrderBy(t => t.Metadata.Name, StringComparer.Ordinal)
            .Select(t => new ComboBoxItem
            {
                Content = $"{t.Metadata.Name} ({t.Metadata.Vendor}) v{t.Metadata.Version}",
                Tag = t.Metadata.Id
            })
            .ToList();
        CmbTarget.ItemsSource = items;

        var wanted = defaultTargetId ?? AllwinnerF1C100STarget.TargetId;
        CmbTarget.SelectedItem = items.FirstOrDefault(i => Equals(i.Tag as string, wanted)) ?? items.FirstOrDefault();
    }

    private IHardwareTarget? SelectedTarget()
    {
        if (CmbTarget.SelectedItem is not ComboBoxItem item) return null;
        var id = item.Tag as string;
        return _targets.FirstOrDefault(t => string.Equals(t.Metadata.Id, id, StringComparison.Ordinal));
    }

    private void UpdateSummary()
    {
        var t = SelectedTarget();
        if (t == null)
        {
            TxtSumVendor.Text = TxtSumArch.Text = TxtSumResolution.Text = "";
            TxtSumDepth.Text = TxtSumRam.Text = TxtSumFlash.Text = "";
            return;
        }

        TxtSumVendor.Text = t.Metadata.Vendor;
        TxtSumArch.Text = t.Metadata.Architecture;
        TxtSumResolution.Text = $"{t.Display.DefaultWidth} × {t.Display.DefaultHeight}";
        TxtSumDepth.Text = string.Join(" / ", t.Display.SupportedColorDepths);
        TxtSumRam.Text = FormatBytes(t.Resources.RamSizeBytes);
        TxtSumFlash.Text = FormatBytes(t.Resources.FlashSizeBytes);
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.#} MiB"
        : $"{bytes / 1024.0:0.#} KiB";

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = AppLocalization.Service[Lang.NewProjectLocation],
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path)) TxtLocation.Text = path;
    }

    private void OnCreateClicked(object? sender, RoutedEventArgs e)
    {
        TxtError.IsVisible = false;

        var name = (TxtProjectName.Text ?? "").Trim();
        if (name.Length == 0)
        {
            ShowError(AppLocalization.Service[Lang.NewProjectErrName]);
            TxtProjectName.Focus();
            return;
        }

        var dir = (TxtLocation.Text ?? "").Trim();
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            ShowError(AppLocalization.Service[Lang.NewProjectErrLocation]);
            TxtLocation.Focus();
            return;
        }

        var fileName = MainWindow.SanitizeFileName(name) + ".hmi";
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path))
        {
            // 不覆盖：同名工程被静默替换比"多一步改名"代价大得多
            ShowError(AppLocalization.Service.Format(Lang.NewProjectErrExists, fileName));
            return;
        }

        Result = new NewProjectRequest(name, SelectedTarget()?.Metadata.Id ?? AllwinnerF1C100STarget.TargetId, path);
        Close();
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.IsVisible = true;
    }
}

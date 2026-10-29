using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HmiEditor.App.Controls;
using HmiEditor.App.Design;
using HmiEditor.App.Resources;
using HmiEditor.App.Services;
using HmiEditor.App;
using HmiEditor.App.Views.Editors;
using HmiEditor.Core.Design;
using HmiEditor.Core.Models;
using HmiEditor.Core.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace HmiEditor.App.Views;

public partial class PropertyPanelView : UserControl
{
    // ── 控件功能参数（按控件类型显隐）──

    /// <summary>加载控件的功能参数：按类型显示相关行并恢复字段值。</summary>
    private void LoadControlParameters(HmiControl control)
    {
        // 先全部隐藏
        SetParamRow(LblMinValue, MinValueBox, false);
        SetParamRow(LblMaxValue, MaxValueBox, false);
        SetParamRow(LblStepValue, StepValueBox, false);
        SetParamRow(LblOptions, OptionsBox, false);
        SetParamRow(LblSelectedIndex, SelectedIndexBox, false);
        SetParamRow(LblPlaceholder, PlaceholderBox, false);
        SetParamRow(LblMaxLength, MaxLengthBox, false);
        SetParamRow(LblInputDecimals, InputDecimalsBox, false);
        SetParamRow(LblPassword, PasswordCheck, false);
        SetParamRow(LblTickCount, TickCountBox, false);
        SetParamRow(LblRowCount, RowCountBox, false);
        SetParamRow(LblColCount, ColCountBox, false);
        SetParamRow(LblHeader, HeaderBox, false);
        SetParamRow(LblTabTitles, TabTitlesBox, false);
        SetParamRow(LblStretch, StretchCombo, false);
        SetParamRow(LblBrightness, BrightnessBox, false);
        SetParamRow(LblSpinSpeed, SpinSpeedBox, false);
        SetParamRow(LblImageAsset, ImageAssetCombo, false);
        SetParamRow(LblSvgAsset, SvgAssetCombo, false);
        // I7: 过程图形控件专属字段
        SetParamRow(LblPipeDirection, PipeDirectionCombo, false);
        SetParamRow(LblPipeFlowDirection, PipeFlowDirectionCombo, false);
        SetParamRow(LblPipeFlowSpeed, PipeFlowSpeedBox, false);
        SetParamRow(LblPipeWall, PipeWallBox, false);
        SetParamRow(LblPipeWidth, PipeWidthBox, false);
        SetParamRow(LblPipeChannel, PipeChannelColorBox, false);
        SetParamRow(LblShapeKind, ShapeKindCombo, false);
        // 数据类控件（行太多，挪到 PropertyPanelView.Parameters.DataViews.cs）
        ShowChartRows(false);
        ShowXyRows(false);
        ShowHistTableRows(false);
        ShowRecipeRows(false);
        ShowAsciiRows(false);
        // 图片族（值换图/帧动画/二维码）与内容型（消息条/枚举文本/滚轮）
        ShowMediaRows(false);
        ShowContentRows(false);
        SetParamRow(LblValveType, ValveTypeCombo, false);
        // chenge-runtime 数据控件参数
        SetParamRow(LblUnit, UnitBox, false);
        SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, false);
        SetParamRow(LblCgAlarmMode, CgAlarmModeCombo, false);
        SetParamRow(LblCgAlarmRows, CgAlarmRowsBox, false);
        SetParamRow(LblCgTrendGroup, CgTrendGroupBox, false);
        SetParamRow(LblCgSpanMin, CgSpanMinBox, false);
        SetParamRow(LblCgCurveVars, CgCurveVarsBox, false);
        // 批次 1a：仪表 / 指示灯的细节属性（独立 Expander，行数够，不与参数区挤）
        // 按钮的确认/长按、输入框的提交时机、开关的三色、Label 的文字排版都不在这里 ——
        // 前两组属交互（动作与高级区），后两组属外观（外观样式组）。
        ShowGaugeRows(false);
        ShowLampRows(false);
        // 交互行（动作与高级区）：按钮的确认/长按 + 输入框的提交时机
        ShowButtonRows(false);
        RowInputCommit.IsVisible = false;
        InteractionExpander.IsVisible = false;
        // 批次 1b：进度条 / 加载圈 / 旋钮 / 页签
        SetParamRow(LblBarDirection, BarDirectionCombo, false);
        SetParamRow(LblBarPercent, BarShowPercentCheck, false);
        SetParamRow(LblSpinnerArcLen, SpinnerArcLenBox, false);
        SetParamRow(LblSpinnerArcWidth, SpinnerArcWidthBox, false);
        SetParamRow(LblKnobArcWidth, KnobArcWidthBox, false);
        SetParamRow(LblTabHeight, TabHeightBox, false);
        SetParamRow(LblTabPos, TabPosCombo, false);
        // 「编辑单元格」只有一个按钮、没有配对的行，复位要单独写 —— 漏了它，切到任何
        // 控件都会带着这个按钮（点了也没用，只有表格有单元格）
        EditCellsBtn.IsVisible = false;
        DetailParamExpander.IsVisible = false;
        ResetExclusiveRows();
        ShowCurveRows(false);
        ShowAlarmListRows(false);
        // 批次 2b：实时曲线采样参数
        SetParamRow(LblSampleMs, SampleMsBox, false);
        SetParamRow(LblHistoryS, HistorySBox, false);
        ShowAlarmBarRows(false);

        switch (control.Type)
        {
            case HmiControlType.Slider:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblStepValue, StepValueBox, true);
                break;
            case HmiControlType.Switch:
                // 开关没有数据参数：三段颜色在外观组，开关本身也没有细节行
                break;
            case HmiControlType.Bar:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                // 批次 1b
                SetParamRow(LblBarDirection, BarDirectionCombo, true);
                SetParamRow(LblBarPercent, BarShowPercentCheck, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Label:
                // 文字排版（字形/字距/行距/超长处理）在外观组，本区只剩值显示
                break;
            case HmiControlType.Gauge:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblTickCount, TickCountBox, true);
                ShowGaugeRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Knob:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                // 契约 knob.step：运行时 build_knob 读 step（拖动的粒度），面板必须有入口
                SetParamRow(LblStepValue, StepValueBox, true);
                SetParamRow(LblKnobArcWidth, KnobArcWidthBox, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Dropdown:
                SetParamRow(LblOptions, OptionsBox, true);
                SetParamRow(LblSelectedIndex, SelectedIndexBox, true);
                break;
            case HmiControlType.TextArea:
                // 量程 + 小数位：数值型输入（绑 i16/f32 变量）才用得上，文本型导出时不写
                // 这两组键（见 ChengeWidgetMapper 的 inputNumeric 分支）。
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblInputDecimals, InputDecimalsBox, true);
                SetParamRow(LblPlaceholder, PlaceholderBox, true);
                SetParamRow(LblMaxLength, MaxLengthBox, true);
                SetParamRow(LblPassword, PasswordCheck, true);
                // 提交时机（回车/失焦）是交互，在动作与高级区
                RowInputCommit.IsVisible = true;
                InteractionExpander.IsVisible = true;
                break;
            case HmiControlType.Table:
                SetParamRow(LblRowCount, RowCountBox, true);
                SetParamRow(LblColCount, ColCountBox, true);
                SetParamRow(LblHeader, HeaderBox, true);
                SetParamRow(LblRowHeight, RowHeightBox, true);
                SetParamRow(LblColWidths, ColWidthsBox, true);
                EditCellsBtn.IsVisible = true;
                break;
            case HmiControlType.TabView:
                SetParamRow(LblTabTitles, TabTitlesBox, true);
                SetParamRow(LblSelectedIndex, SelectedIndexBox, true);
                SetParamRow(LblTabHeight, TabHeightBox, true);
                SetParamRow(LblTabPos, TabPosCombo, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Image:
                SetParamRow(LblStretch, StretchCombo, true);
                SetParamRow(LblImageAsset, ImageAssetCombo, true);
                PopulateImageCombo(control.ImageAssetId);
                break;
            case HmiControlType.Svg:
                SetParamRow(LblSvgAsset, SvgAssetCombo, true);
                PopulateSvgCombo();
                break;
            case HmiControlType.Led:
                SetParamRow(LblBrightness, BrightnessBox, true);
                ShowLampRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Spinner:
                SetParamRow(LblSpinSpeed, SpinSpeedBox, true);
                SetParamRow(LblSpinnerArcLen, SpinnerArcLenBox, true);
                SetParamRow(LblSpinnerArcWidth, SpinnerArcWidthBox, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Line:
                // 没有专属行：细线图元是"填充的窄矩形"，没有端点样式可配（LineCap 不导出）
                break;
            // I7: 过程图形控件
            case HmiControlType.Tank:
                // 罐形图元只显示其图元几何参数；量程和刻度没有 runtime 落点。
                break;
            case HmiControlType.Chart:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                ShowChartRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.XyCurve:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                // 采样周期与时间窗（契约 sample_ms / span_s）：导出器写，运行时读
                SetParamRow(LblSampleMs, SampleMsBox, true);
                SetParamRow(LblCgSpanMin, CgSpanMinBox, true);
                ShowXyRows(true);
                ShowCurveRows(true);
                break;
            case HmiControlType.HistoryTable:
                ShowHistTableRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.RecipeTable:
                ShowRecipeRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.AsciiDisplay:
                ShowAsciiRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Shape:
                SetParamRow(LblShapeKind, ShapeKindCombo, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Pipe:
                SetParamRow(LblPipeDirection, PipeDirectionCombo, true);
                SetParamRow(LblPipeFlowDirection, PipeFlowDirectionCombo, true);
                SetParamRow(LblPipeFlowSpeed, PipeFlowSpeedBox, true);
                SetParamRow(LblPipeWall, PipeWallBox, true);
                SetParamRow(LblPipeWidth, PipeWidthBox, true);
                SetParamRow(LblPipeChannel, PipeChannelColorBox, true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.Valve:
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblValveType, ValveTypeCombo, true);
                break;
            // 批次 1a：按钮（二次确认 / 长按 / 禁用表现）—— 全在动作与高级区的交互行为组
            case HmiControlType.Button:
                ShowButtonRows(true);
                InteractionExpander.IsVisible = true;
                break;
            // chenge-runtime 目标新增
            case HmiControlType.Value:
                // 值显示是只读的：量程由绑定变量的 min/max 决定，导出器也不写，
                // 面板上不再显示「量程下限/上限」。
                SetParamRow(LblUnit, UnitBox, true);
                break;
            case HmiControlType.AlarmBar:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);
                ShowAlarmBarRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.AlarmList:
                SetParamRow(LblCgAlarmGroup, CgAlarmGroupBox, true);
                SetParamRow(LblCgAlarmMode, CgAlarmModeCombo, true);
                SetParamRow(LblCgAlarmRows, CgAlarmRowsBox, true);
                ShowAlarmListRows(true);
                DetailParamExpander.IsVisible = true;
                break;
            case HmiControlType.RealtimeCurve:
                SetParamRow(LblCgSpanMin, CgSpanMinBox, true);
                SetParamRow(LblCgCurveVars, CgCurveVarsBox, true);
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                SetParamRow(LblSampleMs, SampleMsBox, true);
                SetParamRow(LblHistoryS, HistorySBox, true);
                ShowCurveRows(true);
                break;
            case HmiControlType.HistoryCurve:
                SetParamRow(LblCgTrendGroup, CgTrendGroupBox, true);
                SetParamRow(LblCgSpanMin, CgSpanMinBox, true);
                SetParamRow(LblCgCurveVars, CgCurveVarsBox, true);
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                ShowCurveRows(true);
                break;
            // I10: 基础绘图图形
            case HmiControlType.Arc:
                // 弧的几何角度在「专属参数」区（ArcStartBox/ArcEndBox），这里只剩进度量程 min/max，
                // 两者不再共用一个字段。
                SetParamRow(LblMinValue, MinValueBox, true);
                SetParamRow(LblMaxValue, MaxValueBox, true);
                break;
            // 图片族（值换图/帧动画/二维码）与内容型（消息条/枚举文本/滚轮）的专属行：
            // 都在「控件功能参数」里（与 min/max/options 同区），没有「控件细节」行。
            case HmiControlType.StateImage:
            case HmiControlType.FrameAnim:
            case HmiControlType.QrCode:
                ShowMediaRowsFor(control.Type);
                break;
            case HmiControlType.Message:
            case HmiControlType.EnumText:
            case HmiControlType.Roller:
                ShowContentRowsFor(control.Type);
                break;
            default:
                break;
        }

        // I10: 非 Arc 类型恢复 Min/Max 行常规标签（避免上一种类型切换遗留的角度标签）
        if (control.Type != HmiControlType.Arc)
        {
            LblMinValue.Text = AppLocalization.Service[Lang.PropParamMin];
            LblMaxValue.Text = AppLocalization.Service[Lang.PropParamMax];
        }

        // 专属参数（运行时已消费但原先没有编辑入口）：显隐与回填都按类型算
        ShowExclusiveRows(control);

        // 字体族 / 字号 / 对齐的显隐跟着「内容 + 外观样式」那张表走（见 AppearanceRows.cs），
        // 免得两处各写一份名单后漂移。

        // 旋转/镜像：只有运行时 apply_transform 支持的档位才显示（图片 / 图元 / 文本类），
        // 其余类型连行都不出现 —— 免得用户设了之后只在导出时撞 Error。
        var hasTransform = AppearanceSupport.HasTransform(control.Type);
        // 镜像只有图片/顶点图元能表达：文本类镜像会让导出器报 Error（整页建不起来），
        // 所以这一行按 HasMirror 单独判，别跟着"能旋转"一起出现。
        var hasMirror = hasTransform && AppearanceSupport.HasMirror(control.Type);
        SetParamRow(LblRotation, RotationBox, hasTransform);
        SetParamRow(LblRotationDir, RotationDirCombo, hasTransform);
        SetParamRow(LblMirrorH, MirrorHCheck, hasMirror);
        SetParamRow(LblMirrorV, MirrorVCheck, hasMirror);
        BtnResetRotation.IsVisible = hasTransform;

        // 「控件功能参数」有没有行可显示：按行上的实际结果收口。旋转/镜像虽然在上面一起切了
        // 显隐，但它们画在「位置与大小」区 —— 把它们算进来只会给"只有变换行"的类型留一张空卡片。
        ControlParamExpander.IsVisible = ControlParamGrid.Children.Any(c => c.IsVisible);

        // 恢复字段值。这里不按 ControlParamExpander 的可见性早退：旋转/镜像行、图上放着但本区
        // 不显示的字段都要回填，否则切类型后编辑器里留着上一个控件的值。
        RotationBox.Value = control.Rotation;
        SelectComboByTag(RotationDirCombo, control.RotationDirection, 0);
        MirrorHCheck.IsChecked = control.MirrorX;
        MirrorVCheck.IsChecked = control.MirrorY;
        MinValueBox.Value = control.MinValue;
        MaxValueBox.Value = control.MaxValue;
        StepValueBox.Value = control.StepValue;
        SelectedIndexBox.Value = control.SelectedIndex;
        UnitBox.Text = control.Unit ?? "";
        CgAlarmGroupBox.Text = control.AlarmGroupFilter ?? "";
        SelectComboByTag(CgAlarmModeCombo, control.AlarmMode, 0);
        CgAlarmRowsBox.Value = control.AlarmMaxRows;
        CgTrendGroupBox.Text = control.TrendGroup ?? "";
        CgSpanMinBox.Value = control.SpanMin;
        CgCurveVarsBox.Text = control.CurveVariableIds ?? "";
        FillGaugeLampButtonFields(control);
        SwitchColorOnBox.Text = control.SwitchColorOn ?? "";
        SwitchColorOffBox.Text = control.SwitchColorOff ?? "";
        SwitchKnobColorBox.Text = control.SwitchKnobColor ?? "";
        SelectComboByTag(BarDirectionCombo, control.BarDirection, 0);
        BarShowPercentCheck.IsChecked = control.BarShowPercent;
        SpinnerArcLenBox.Value = control.SpinnerArcLen;
        SpinnerArcWidthBox.Value = control.SpinnerArcWidth;
        SelectComboByTag(LabelFontStyleCombo, control.LabelFontStyle, 0);
        LetterSpacingBox.Value = control.LabelLetterSpacing;
        LineSpacingBox.Value = control.LabelLineSpacing;
        SelectComboByTag(InputCommitCombo, control.InputCommit, 0);
        KnobArcWidthBox.Value = control.KnobArcWidth;
        TabHeightBox.Value = control.TabHeight;
        SelectComboByTag(TabPosCombo, control.TabPos, 0);
        FillCurveFields(control);
        FillAlarmFields(control);
        PipeWallBox.Value = control.PipeWall;
        PipeWidthBox.Value = Math.Clamp(control.PipeWidth, 4, 200);
        PipeChannelColorBox.Text = control.PipeChannelColor ?? "";
        SelectComboByTag(ShapeKindCombo, control.ShapeKind, 0);
        FillDataViewFields(control);
        FillMediaFields(control);
        FillContentFields(control);

        OptionsBox.Text = control.Options ?? "";
        PlaceholderBox.Text = control.PlaceholderText ?? "";
        MaxLengthBox.Value = control.MaxLength;
        InputDecimalsBox.Value = Math.Clamp(control.ValueDecimalPlaces, 0, 6);
        PasswordCheck.IsChecked = control.PasswordMode;
        TickCountBox.Value = control.MajorTickCount;
        RowCountBox.Value = control.RowCount;
        ColCountBox.Value = control.ColumnCount;
        HeaderBox.Text = control.HeaderTexts ?? "";
        RowHeightBox.Value = control.RowHeight;
        ColWidthsBox.Text = control.ColumnWidths ?? "";
        TabTitlesBox.Text = control.TabTitles ?? "";
        SelectComboByTag(StretchCombo, control.StretchMode, 1);
        BrightnessBox.Value = control.Brightness;
        ShowTicksCheck.IsChecked = control.ShowTicks;
        ShowPointerCheck.IsChecked = control.EffectiveShowPointer();
        SpinSpeedBox.Value = control.SpinSpeed;
        SelectComboByTag(LongModeCombo, control.LabelLongMode, 0);

        SelectComboByTag(PipeDirectionCombo, control.PipeDirection, 0);
        SelectComboByTag(PipeFlowDirectionCombo, control.PipeFlowDirection, 0);
        PipeFlowSpeedBox.Value = control.PipeFlowSpeed;
        SelectComboByTag(ValveTypeCombo, control.ValveType, 0);
    }

    /// <summary>成对显隐参数行（标签 + 输入控件）。</summary>
    private static void SetParamRow(Control label, Control input, bool visible)
    {
        label.IsVisible = visible;
        input.IsVisible = visible;
    }

    /// <summary>重置旋转角度与镜像（角度归零、镜像关闭），即时预览并通知落盘。</summary>
    private void OnResetRotationClicked(object? sender, RoutedEventArgs e)
    {
        if (_currentControl == null) return;
        _currentControl.Rotation = 0;
        _currentControl.RotationDirection = null;
        _currentControl.MirrorX = false;
        _currentControl.MirrorY = false;
        RotationBox.Value = 0;
        RotationDirCombo.SelectedIndex = 0;
        MirrorHCheck.IsChecked = false;
        MirrorVCheck.IsChecked = false;
        PropertyChanged?.Invoke(this, _currentControl);
    }

    /// <summary>
    /// 打开表格单元格编辑器；编辑完成后通知画布重绘并落盘。
    /// 对话框异常由 UiOperationGuard 兜底（本视图没有状态行，回落到主窗口状态栏）。
    /// </summary>
    private void OnEditCellsClicked(object? sender, RoutedEventArgs e) =>
        UiOperationGuard.Run(Lang.UiOpEditGridCell, async () =>
        {
            if (_currentControl == null) return;
            if (TopLevel.GetTopLevel(this) is not Window owner) return;

            var variables = _variableManager?.Variables?.ToList() ?? new List<Variable>();
            var dialog = new TableCellEditorDialog(_currentControl, variables);
            await dialog.ShowDialog(owner);

            // 与 ImageAsset/SvgAsset 编辑一致：通知 MainWindow 重绘画布 + 写入撤销栈
            PropertyChanged?.Invoke(this, _currentControl);
        });

    /// <summary>按 Tag 选择 ComboBox 项，未匹配时回退到指定索引。</summary>
    private static void SelectComboByTag(ComboBox combo, string? tag, int fallbackIndex)
    {
        var index = -1;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }
        combo.SelectedIndex = index >= 0 ? index : fallbackIndex;
    }

    /// <summary>读取 ComboBox 选中项的 Tag。</summary>
    private static string? GetComboTag(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
}

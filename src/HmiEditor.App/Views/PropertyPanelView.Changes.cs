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
    // ── 属性变更 ──

    private void OnPropertyChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isUpdating || _currentControl == null) return;
        // 防御：Dock 重组期间控件可能已被卸载，延迟的 TextChange 事件不应继续处理
        if (Parent == null) return;
        SaveProperties();
    }

    private void OnNumberChanged(object? sender, FANumberBoxValueChangedEventArgs e)
    {
        if (_isUpdating || _currentControl == null) return;
        if (Parent == null) return;
        SaveProperties();
    }

    private void OnTooltipChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isUpdating || _currentControl == null || Parent == null) return;
        ValidateAndWrite(nameof(HmiControl.Tooltip), string.IsNullOrWhiteSpace(TooltipBox.Text) ? null : TooltipBox.Text.Trim(),
            v => _currentControl.Tooltip = v);
        PropertyChanged?.Invoke(this, _currentControl);
    }

    /// <summary>
    /// P1-3: 校验属性值后写回 —— CustomValidator 返回错误消息则跳过写回（保留原值）并记录日志。
    /// 校验通过后执行 writeAction 写回模型。
    /// </summary>
    /// <summary>
    /// 校验通过后写回模型。**面板编辑器的所有写回都必须走这里** —— 否则可插拔的
    /// <see cref="IPropertyValidator"/> 会变成"部分属性有效"，插件作者无法预测
    /// 自己的规则何时被调用（P1-3 的完整语义）。
    ///
    /// <para>注意区分两类写入：编辑器改动（本方法）与**程序化命令**（重置变换、
    /// 选择图片资源、切换绑定模式等）。后者是用户明确下达的指令，不该被属性校验器
    /// 静默否决，因此仍直接写模型。</para>
    /// </summary>
    private void ValidateAndWrite<T>(string propertyName, T value, Action<T> writeAction)
    {
        if (_currentControl == null) return;
        var message = CustomValidator?.Validate(_currentControl, propertyName, value);
        if (message != null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PropertyPanel] 属性校验未通过 {propertyName}: {message}（保留原值）");
            return;
        }
        writeAction(value);
    }

    /// <summary>
    /// 外部改了几何（画布拖动/对齐/撤销）后，把坐标尺寸框同步到模型现值。
    ///
    /// <para><b>为什么必须做</b>：<see cref="SaveProperties"/> 会把所有可见框一并写回模型。
    /// 拖动后若面板里还是拖动前的 X/Y，用户接着改文本（不失焦）时，那对陈旧坐标会被
    /// 一并写回 —— 刚拖好的位置被覆盖回原位（实测现象：改完文本按钮跳回原处）。</para>
    /// </summary>
    public void RefreshGeometry()
    {
        if (_currentControl == null) return;
        _isUpdating = true;   // 写框会 raise ValueChanged，别让它反过来再 SaveProperties
        try
        {
            PropX.Value = _currentControl.X;
            PropY.Value = _currentControl.Y;
            PropWidth.Value = _currentControl.Width;
            PropHeight.Value = _currentControl.Height;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>从所有编辑器控件读取值并写回当前选中的 HmiControl。</summary>
    private void SaveProperties()
    {
        if (_currentControl == null) return;

        // P1-3: 可注入即时校验 —— 对易越界/易出格式错误的属性先校验，失败则跳过写回（保留原值）
        // 校验通过后仍走静态 PropertyValidator 钳制（合法性兜底）。
        // ⚠ 名字再走一次 ControlNameDefaults.ToPersisted：面板里显示的是**本地化别名**
        //   （`按钮1`），落盘的必须是**中性名**（`Button1`）—— 否则"照着自己看到的名字改一遍"
        //   会把工程写回成随语言变的形状（登记 #61r，阶段 159）。
        ValidateAndWrite(nameof(HmiControl.Name), PropName.Text, v =>
            _currentControl.Name = Services.ControlNameDefaults.ToPersisted(
                PropertyValidator.SanitizeName(v ?? "", _currentControl.Name), _currentControl.Type));
        // 每一行只在**本类型可见**时才写回：隐藏行里的编辑器仍是上一次选中控件的值，
        // 无条件写回会把它灌进新控件（旋转 90° 的 Label → 切到表格改个名字，表格也被转 90°）。
        if (RowBgColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.BgColor), PropBgColor.Text, v =>
                _currentControl.BgColor = PropertyValidator.SanitizeColor(v));
        if (RowTextColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.TextColor), PropTextColor.Text, v =>
                _currentControl.TextColor = PropertyValidator.SanitizeColor(v));
        if (RowBorderColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.BorderColor), PropBorderColor.Text, v =>
                _currentControl.BorderColor = PropertyValidator.SanitizeColor(v));
        if (RowIndicatorColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.IndicatorColor), IndicatorColorBox.Text, v =>
                _currentControl.IndicatorColor = PropertyValidator.SanitizeColor(v));

        // FF6: 所有属性值通过 PropertyValidator 验证后再写回模型
        ValidateAndWrite(nameof(HmiControl.X), PropertyValidator.ClampPosition(PropX.Value), v => _currentControl.X = v);
        ValidateAndWrite(nameof(HmiControl.Y), PropertyValidator.ClampPosition(PropY.Value), v => _currentControl.Y = v);
        ValidateAndWrite(nameof(HmiControl.Width), PropertyValidator.ClampSize(PropWidth.Value), v => _currentControl.Width = v);
        ValidateAndWrite(nameof(HmiControl.Height), PropertyValidator.ClampSize(PropHeight.Value), v => _currentControl.Height = v);
        if (RowText.IsVisible)
            ValidateAndWrite(nameof(HmiControl.Text), string.IsNullOrEmpty(PropText.Text) ? null : PropText.Text, v => _currentControl.Text = v);
        if (RowSwitchTextOff.IsVisible)
            WriteSwitchTextOff();
        if (RowRadius.IsVisible)
            ValidateAndWrite(nameof(HmiControl.Radius), PropertyValidator.ClampRadius((int)PropRadius.Value), v => _currentControl.Radius = v);
        if (RowBorderWidth.IsVisible)
            ValidateAndWrite(nameof(HmiControl.BorderWidth), PropertyValidator.ClampBorderWidth((int)PropBorderWidth.Value), v => _currentControl.BorderWidth = v);
        if (RowIsChecked.IsVisible)
            ValidateAndWrite(nameof(HmiControl.IsChecked), PropIsChecked.IsChecked ?? false, v => _currentControl.IsChecked = v);
        ValidateAndWrite(nameof(HmiControl.IsVisible), PropVisible.IsChecked ?? true, v => _currentControl.IsVisible = v);
        ValidateAndWrite(nameof(HmiControl.IsEnabled), PropEnabled.IsChecked ?? true, v => _currentControl.IsEnabled = v);
        ValidateAndWrite(nameof(HmiControl.Tooltip), string.IsNullOrWhiteSpace(TooltipBox.Text) ? null : TooltipBox.Text.Trim(), v => _currentControl.Tooltip = v);

        // 字体 & 对齐
        if (RowFontSize.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FontSize), (int)PropFontSize.Value, v =>
                _currentControl.FontSize = v);
        if (RowTextAlign.IsVisible && PropTextAlign.SelectedItem is ComboBoxItem ci_h && ci_h.Tag is string tagH)
            ValidateAndWrite(nameof(HmiControl.TextAlign), Enum.Parse<TextAlignMode>(tagH), v => _currentControl.TextAlign = v);
        if (RowTextVAlign.IsVisible && PropTextVAlign.SelectedItem is ComboBoxItem ci_v && ci_v.Tag is string tagV)
            ValidateAndWrite(nameof(HmiControl.TextVAlign), Enum.Parse<TextVAlignMode>(tagV), v => _currentControl.TextVAlign = v);
        // 字体族：面板恒隐藏（导出器不写 FontFamily），只在可见时写
        if (RowFontFamily.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FontFamily), PropFontFamily.SelectedItem is ComboBoxItem ci_f ? (ci_f.Tag as string) : null, v => _currentControl.FontFamily = v);
        // 指示器颜色（仅显示型控件加载）与不透明度（通用）
        if (RowOpacity.IsVisible)
            ValidateAndWrite(nameof(HmiControl.Opacity), OpacityBox.Value, v =>
                _currentControl.Opacity = (double)v!);

        // 文字排版（字形 / 字距 / 行距 / 超长处理）：行在外观组里，按行显隐写回
        if (RowLabelFontStyle.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelFontStyle), GetComboTag(LabelFontStyleCombo), v => _currentControl.LabelFontStyle = v);
        if (RowLetterSpacing.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelLetterSpacing), (int)LetterSpacingBox.Value, v => _currentControl.LabelLetterSpacing = v);
        if (RowLineSpacing.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelLineSpacing), (int)LineSpacingBox.Value, v => _currentControl.LabelLineSpacing = v);
        if (RowLongMode.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelLongMode), GetComboTag(LongModeCombo), v => _currentControl.LabelLongMode = v);

        // 开关三段颜色（color_on / color_off / knob_color）：同样按行显隐写回
        if (RowSwitchColorOn.IsVisible)
            ValidateAndWrite(nameof(HmiControl.SwitchColorOn), string.IsNullOrWhiteSpace(SwitchColorOnBox.Text) ? null : SwitchColorOnBox.Text.Trim(), v => _currentControl.SwitchColorOn = v);
        if (RowSwitchColorOff.IsVisible)
            ValidateAndWrite(nameof(HmiControl.SwitchColorOff), string.IsNullOrWhiteSpace(SwitchColorOffBox.Text) ? null : SwitchColorOffBox.Text.Trim(), v => _currentControl.SwitchColorOff = v);
        if (RowSwitchKnob.IsVisible)
            ValidateAndWrite(nameof(HmiControl.SwitchKnobColor), string.IsNullOrWhiteSpace(SwitchKnobColorBox.Text) ? null : SwitchKnobColorBox.Text.Trim(), v => _currentControl.SwitchKnobColor = v);

        // 值显示配置（仅显示型控件加载，面板可见时保存）
        if (ValueDisplayExpander.IsVisible)
        {
            ValidateAndWrite(nameof(HmiControl.ValueDisplayType), ParseValueDisplayType( ValueDisplayTypeCombo.SelectedItem as ComboBoxItem), v => _currentControl.ValueDisplayType = v);
            ValidateAndWrite(nameof(HmiControl.ValueScaleFactor), ValueScaleBox.Value, v => _currentControl.ValueScaleFactor = v);
            // 小数位下拉在部分类型上被 Display.cs 隐藏（值类型不是 Float/Auto）：
            // 隐藏时它的选中项是上一次控件的，写回会覆盖本类型真正的小数位。
            if (ValueDecimalsCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ValueDecimalPlaces), ParseValueDecimalPlaces( ValueDecimalsCombo.SelectedItem as ComboBoxItem), v => _currentControl.ValueDecimalPlaces = v);
            ValidateAndWrite(nameof(HmiControl.ValuePrefix), string.IsNullOrEmpty(ValuePrefixBox.Text) ? null : ValuePrefixBox.Text, v => _currentControl.ValuePrefix = v);
            ValidateAndWrite(nameof(HmiControl.ValueSuffix), string.IsNullOrEmpty(ValueSuffixBox.Text) ? null : ValueSuffixBox.Text, v => _currentControl.ValueSuffix = v);
        }

        // 「数据与显示」区各行的写回。闸门是**这一区是否显示**，区内的每个字段仍逐行判自己
        // 的可见性（隐藏行里留的是上一个控件的值）。不能只挂「控件功能参数」那一个分组的可见性：
        // 历史表 / 配方表 / 报警条的数据行整块在兄弟分组里，挂上去就是"改了不生效"。
        if (DataDisplaySection.IsVisible)
        {
            if (MinValueBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.MinValue), MinValueBox.Value, v => _currentControl.MinValue = v);
            if (MaxValueBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.MaxValue), MaxValueBox.Value, v => _currentControl.MaxValue = v);
            if (StepValueBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.StepValue), StepValueBox.Value, v => _currentControl.StepValue = v);
            if (SelectedIndexBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.SelectedIndex), (int)SelectedIndexBox.Value, v => _currentControl.SelectedIndex = v);
            if (OptionsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.Options), string.IsNullOrWhiteSpace(OptionsBox.Text) ? null : OptionsBox.Text.Replace("\r\n", "\n"), v => _currentControl.Options = v);
            if (PlaceholderBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PlaceholderText), string.IsNullOrEmpty(PlaceholderBox.Text) ? null : PlaceholderBox.Text, v => _currentControl.PlaceholderText = v);
            if (MaxLengthBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.MaxLength), (int)MaxLengthBox.Value, v => _currentControl.MaxLength = v);
            if (InputDecimalsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ValueDecimalPlaces), (int)InputDecimalsBox.Value, v => _currentControl.ValueDecimalPlaces = v);
            if (PasswordCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PasswordMode), PasswordCheck.IsChecked ?? false, v => _currentControl.PasswordMode = v);
            if (TickCountBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.MajorTickCount), (int)TickCountBox.Value, v => _currentControl.MajorTickCount = v);
            if (RowCountBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.RowCount), (int)RowCountBox.Value, v => _currentControl.RowCount = v);
            if (ColCountBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ColumnCount), (int)ColCountBox.Value, v => _currentControl.ColumnCount = v);
            if (HeaderBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HeaderTexts), string.IsNullOrWhiteSpace(HeaderBox.Text) ? null : HeaderBox.Text.Trim(), v => _currentControl.HeaderTexts = v);
            if (RowHeightBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.RowHeight), RowHeightBox.Value, v => _currentControl.RowHeight = v);
            if (ColWidthsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ColumnWidths), string.IsNullOrWhiteSpace(ColWidthsBox.Text) ? null : ColWidthsBox.Text.Trim(), v => _currentControl.ColumnWidths = v);
            if (TabTitlesBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.TabTitles), string.IsNullOrWhiteSpace(TabTitlesBox.Text) ? null : TabTitlesBox.Text.Trim(), v => _currentControl.TabTitles = v);
            // chenge-runtime 数据控件参数（面板可见时才写，避免把无关字段清空）
            if (CgAlarmGroupBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmGroupFilter), string.IsNullOrWhiteSpace(CgAlarmGroupBox.Text) ? null : CgAlarmGroupBox.Text.Trim(), v => _currentControl.AlarmGroupFilter = v);
            if (CgAlarmModeCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmMode), GetComboTag(CgAlarmModeCombo), v => _currentControl.AlarmMode = v);
            if (CgAlarmRowsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmMaxRows), (int)CgAlarmRowsBox.Value, v => _currentControl.AlarmMaxRows = v);
            if (CgTrendGroupBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.TrendGroup), string.IsNullOrWhiteSpace(CgTrendGroupBox.Text) ? null : CgTrendGroupBox.Text.Trim(), v => _currentControl.TrendGroup = v);
            if (CgSpanMinBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.SpanMin), (int)CgSpanMinBox.Value, v => _currentControl.SpanMin = v);
            if (CgCurveVarsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveVariableIds), string.IsNullOrWhiteSpace(CgCurveVarsBox.Text) ? null : CgCurveVarsBox.Text.Trim(), v => _currentControl.CurveVariableIds = v);
            // 批次 1a：仪表 / 指示灯 / 按钮的细节属性（面板可见时才写）
            if (GaugeArcWidthBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.GaugeArcWidth), (int)GaugeArcWidthBox.Value, v => _currentControl.GaugeArcWidth = v);
            if (GaugeTickLenBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.GaugeTickLen), (int)GaugeTickLenBox.Value, v => _currentControl.GaugeTickLen = v);
            if (GaugeNeedleLenBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.GaugeNeedleLen), GaugeNeedleLenBox.Value, v => _currentControl.GaugeNeedleLen = v);
            if (GaugeNeedleWidthBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.GaugeNeedleWidth), (int)GaugeNeedleWidthBox.Value, v => _currentControl.GaugeNeedleWidth = v);
            if (GaugeCenterRadiusBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.GaugeCenterRadius), (int)GaugeCenterRadiusBox.Value, v => _currentControl.GaugeCenterRadius = v);
            if (GaugeShowValueCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.GaugeShowValue), GaugeShowValueCheck.IsChecked == true, v => _currentControl.GaugeShowValue = v);
            if (LampSizeBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.LampSize), (int)LampSizeBox.Value, v => _currentControl.LampSize = v);
            // 批次 1b：进度条 / 加载圈 / 旋钮 / 页签
            if (BarDirectionCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.BarDirection), GetComboTag(BarDirectionCombo), v => _currentControl.BarDirection = v);
            if (BarShowPercentCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.BarShowPercent), BarShowPercentCheck.IsChecked == true, v => _currentControl.BarShowPercent = v);
            if (SpinnerArcLenBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.SpinnerArcLen), (int)SpinnerArcLenBox.Value, v => _currentControl.SpinnerArcLen = v);
            if (SpinnerArcWidthBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.SpinnerArcWidth), (int)SpinnerArcWidthBox.Value, v => _currentControl.SpinnerArcWidth = v);
            if (KnobArcWidthBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.KnobArcWidth), (int)KnobArcWidthBox.Value, v => _currentControl.KnobArcWidth = v);
            if (TabHeightBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.TabHeight), (int)TabHeightBox.Value, v => _currentControl.TabHeight = v);
            if (TabPosCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.TabPos), GetComboTag(TabPosCombo), v => _currentControl.TabPos = v);
            // 批次 2：曲线外观与刻度（面板可见时才写）
            if (CurveAxisColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveAxisColor), string.IsNullOrWhiteSpace(CurveAxisColorBox.Text) ? null : CurveAxisColorBox.Text.Trim(), v => _currentControl.CurveAxisColor = v);
            if (CurveBgColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveBgColor), string.IsNullOrWhiteSpace(CurveBgColorBox.Text) ? null : CurveBgColorBox.Text.Trim(), v => _currentControl.CurveBgColor = v);
            if (CurveBorderColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveBorderColor), string.IsNullOrWhiteSpace(CurveBorderColorBox.Text) ? null : CurveBorderColorBox.Text.Trim(), v => _currentControl.CurveBorderColor = v);
            if (CurveGridColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveGridColor), string.IsNullOrWhiteSpace(CurveGridColorBox.Text) ? null : CurveGridColorBox.Text.Trim(), v => _currentControl.CurveGridColor = v);
            if (CurveTickColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveTickColor), string.IsNullOrWhiteSpace(CurveTickColorBox.Text) ? null : CurveTickColorBox.Text.Trim(), v => _currentControl.CurveTickColor = v);
            if (CurveLabelColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveLabelColor), string.IsNullOrWhiteSpace(CurveLabelColorBox.Text) ? null : CurveLabelColorBox.Text.Trim(), v => _currentControl.CurveLabelColor = v);
            if (CurveGridCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveGrid), CurveGridCheck.IsChecked == true, v => _currentControl.CurveGrid = v);
            if (CurveLegendCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveLegend), CurveLegendCheck.IsChecked == true, v => _currentControl.CurveLegend = v);
            if (CurveShowAxisCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveShowAxis), CurveShowAxisCheck.IsChecked == true, v => _currentControl.CurveShowAxis = v);
            if (CurveShowLabelsCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveShowLabels), CurveShowLabelsCheck.IsChecked == true, v => _currentControl.CurveShowLabels = v);
            if (CurveDirectionCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveDirection), GetComboTag(CurveDirectionCombo), v => _currentControl.CurveDirection = v);
            if (CurveXFormatCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXFormat), GetComboTag(CurveXFormatCombo), v => _currentControl.CurveXFormat = v);
            if (CurveXDivBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXDiv), (int)CurveXDivBox.Value, v => _currentControl.CurveXDiv = v);
            if (CurveXMinorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXMinor), (int)CurveXMinorBox.Value, v => _currentControl.CurveXMinor = v);
            if (CurveXLabelStepBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveXLabelStep), (int)CurveXLabelStepBox.Value, v => _currentControl.CurveXLabelStep = v);
            if (CurveYDivBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYDiv), (int)CurveYDivBox.Value, v => _currentControl.CurveYDiv = v);
            if (CurveYMinorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYMinor), (int)CurveYMinorBox.Value, v => _currentControl.CurveYMinor = v);
            if (CurveYDecimalsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYDecimals), (int)CurveYDecimalsBox.Value, v => _currentControl.CurveYDecimals = v);
            if (CurveYDigitsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveYDigits), (int)CurveYDigitsBox.Value, v => _currentControl.CurveYDigits = v);
            if (CurveY2MinBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Min), (int)CurveY2MinBox.Value, v => _currentControl.CurveY2Min = v);
            if (CurveY2MaxBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Max), (int)CurveY2MaxBox.Value, v => _currentControl.CurveY2Max = v);
            if (CurveY2DecimalsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Decimals), (int)CurveY2DecimalsBox.Value, v => _currentControl.CurveY2Decimals = v);
            if (CurveY2DigitsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveY2Digits), (int)CurveY2DigitsBox.Value, v => _currentControl.CurveY2Digits = v);
            if (CurveLastMinBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.CurveRangeLastMin), (int)CurveLastMinBox.Value, v => _currentControl.CurveRangeLastMin = v);
            // 批次 2c：报警列表显示细节
            if (AlarmOrderCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmOrder), GetComboTag(AlarmOrderCombo), v => _currentControl.AlarmOrder = v);
            if (AlarmAckOnCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmAckOn), GetComboTag(AlarmAckOnCombo), v => _currentControl.AlarmAckOn = v);
            if (AlarmGridCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmGrid), GetComboTag(AlarmGridCombo), v => _currentControl.AlarmGrid = v);
            if (AlarmRowHeightBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmRowHeight), (int)AlarmRowHeightBox.Value, v => _currentControl.AlarmRowHeight = v);
            if (AlarmScrollbarCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmScrollbar), AlarmScrollbarCheck.IsChecked == true, v => _currentControl.AlarmScrollbar = v);
            if (AlarmRefreshBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmRefreshMs), (int)AlarmRefreshBox.Value, v => _currentControl.AlarmRefreshMs = v);
            if (AlarmBlinkCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmBlink), AlarmBlinkCheck.IsChecked == true, v => _currentControl.AlarmBlink = v);
            if (AlarmBlinkMsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmBlinkMs), (int)AlarmBlinkMsBox.Value, v => _currentControl.AlarmBlinkMs = v);
            if (AlarmDateFormatBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmDateFormat), string.IsNullOrWhiteSpace(AlarmDateFormatBox.Text) ? null : AlarmDateFormatBox.Text.Trim(), v => _currentControl.AlarmDateFormat = v);
            if (AlarmTimeFormatBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmTimeFormat), string.IsNullOrWhiteSpace(AlarmTimeFormatBox.Text) ? null : AlarmTimeFormatBox.Text.Trim(), v => _currentControl.AlarmTimeFormat = v);
            // 批次 2b：实时曲线采样参数
            if (SampleMsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.SampleMs), (int)SampleMsBox.Value, v => _currentControl.SampleMs = v);
            if (HistorySBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.HistoryS), (int)HistorySBox.Value, v => _currentControl.HistoryS = v);
            // 批次 2d：报警条
            if (AlarmNormalTextBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmNormalText), string.IsNullOrWhiteSpace(AlarmNormalTextBox.Text) ? null : AlarmNormalTextBox.Text.Trim(), v => _currentControl.AlarmNormalText = v);
            if (AlarmOnlyUnackedCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmOnlyUnacked), AlarmOnlyUnackedCheck.IsChecked == true, v => _currentControl.AlarmOnlyUnacked = v);
            if (AlarmScrollMsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmScrollMs), (int)AlarmScrollMsBox.Value, v => _currentControl.AlarmScrollMs = v);
            if (AlarmScrollCharsBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmScrollChars), (int)AlarmScrollCharsBox.Value, v => _currentControl.AlarmScrollChars = v);
            if (AlarmShowDateCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmShowDate), AlarmShowDateCheck.IsChecked == true, v => _currentControl.AlarmShowDate = v);
            if (AlarmShowTimeCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.AlarmShowTime), AlarmShowTimeCheck.IsChecked == true, v => _currentControl.AlarmShowTime = v);
            if (PipeWallBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeWall), PipeWallBox.Value, v => _currentControl.PipeWall = v);
            if (PipeChannelColorBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeChannelColor), string.IsNullOrWhiteSpace(PipeChannelColorBox.Text) ? null : PipeChannelColorBox.Text.Trim(), v => _currentControl.PipeChannelColor = v);
            if (ShapeKindCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ShapeKind), GetComboTag(ShapeKindCombo), v => _currentControl.ShapeKind = v);
            WriteDataViewFields();
            WriteMediaFields();
            WriteContentFields();
            // StretchMode 仅对图片控件有意义：下拉框隐藏（非图片控件）时不写，避免污染模型
            if (StretchCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.StretchMode), GetComboTag(StretchCombo), v => _currentControl.StretchMode = v);
            if (BrightnessBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.Brightness), (int)BrightnessBox.Value, v => _currentControl.Brightness = v);
            // 显示刻度（仪表 ticks / 曲线 show_ticks）与显示指针（仪表 style）各行独立判可见：
            // 曲线只显示刻度那一行，一起写会把它的 ShowPointer 也改掉（多出一条无意义的撤销）
            if (ShowTicksCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ShowTicks), ShowTicksCheck.IsChecked ?? false, v => _currentControl.ShowTicks = v);
            if (ShowPointerCheck.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ShowPointer), ShowPointerCheck.IsChecked ?? false, v => _currentControl.ShowPointer = v);
            if (SpinSpeedBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.SpinSpeed), (int)SpinSpeedBox.Value, v => _currentControl.SpinSpeed = v);
            if (ImageAssetCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ImageAssetId), GetComboTag(ImageAssetCombo), v => _currentControl.ImageAssetId = v);
            if (PipeDirectionCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeDirection), GetComboTag(PipeDirectionCombo), v => _currentControl.PipeDirection = v);
            if (PipeFlowDirectionCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeFlowDirection), GetComboTag(PipeFlowDirectionCombo), v => _currentControl.PipeFlowDirection = v);
            if (PipeFlowSpeedBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeFlowSpeed), (int)PipeFlowSpeedBox.Value, v => _currentControl.PipeFlowSpeed = v);
            if (PipeWidthBox.IsVisible)
                ValidateAndWrite(nameof(HmiControl.PipeWidth), PipeWidthBox.Value, v => _currentControl.PipeWidth = v);
            if (ValveTypeCombo.IsVisible)
                ValidateAndWrite(nameof(HmiControl.ValveType), GetComboTag(ValveTypeCombo), v => _currentControl.ValveType = v);
        }

        // 这两行的编辑器不在「控件功能参数」里：旋转/镜像画在「位置与大小」，
        // 单位在值显示组。因此不能受本区可见性约束 —— 否则本区一行都不显示的类型
        // （Label / Button / Value）改了它们不生效。
        // 旋转/镜像：运行时只对部分类型支持（AppearanceSupport.HasTransform），
        // 不支持的类型连行都不出现 —— 隐藏行里的旧值绝不能写回新控件。
        if (RotationBox.IsVisible)
        {
            ValidateAndWrite(nameof(HmiControl.Rotation), RotationBox.Value, v => _currentControl.Rotation = v);
            ValidateAndWrite(nameof(HmiControl.RotationDirection), GetComboTag(RotationDirCombo), v => _currentControl.RotationDirection = v);
            ValidateAndWrite(nameof(HmiControl.MirrorX), MirrorHCheck.IsChecked ?? false, v => _currentControl.MirrorX = v);
            ValidateAndWrite(nameof(HmiControl.MirrorY), MirrorVCheck.IsChecked ?? false, v => _currentControl.MirrorY = v);
        }
        if (UnitBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.Unit), string.IsNullOrWhiteSpace(UnitBox.Text) ? null : UnitBox.Text.Trim(), v => _currentControl.Unit = v);

        // 专属参数区：同样按行可见性写回（不可见不写）
        SaveExclusiveRows();

        // 交互行为组在「动作与高级」区，闸门是它自己那几行
        SaveInteractionRows();

        SaveAction();

        // 状态面板 Normal 态的文本与内容区文本保持同步（同一字段 control.Text）
        // 防御：ViewLocator 工厂重建时 VsText 可能尚未初始化
        if (VsText != null && _currentEditingState == VisualState.Normal && VsText.Text != (_currentControl.Text ?? ""))
        {
            _isUpdating = true;
            try { VsText.Text = _currentControl.Text ?? ""; }
            finally { _isUpdating = false; }
        }
    }

    // ── 颜色选择器 ──────────────────────────────────────

    private void OnColorSwatchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Border swatch && swatch.Tag is string propName)
        {
            _activeColorField = propName;
            var currentColor = propName switch
            {
                "PropBgColor" => PropBgColor.Text ?? "",
                "PropTextColor" => PropTextColor.Text ?? "",
                "PropBorderColor" => PropBorderColor.Text ?? "",
                "IndicatorColor" => IndicatorColorBox.Text ?? "",
                "VsBgColor" => VsBgColor.Text ?? "",
                "VsTextColor" => VsTextColor.Text ?? "",
                "VsBorderColor" => VsBorderColor.Text ?? "",
                "ScreenBgColor" => ScreenBgColor.Text ?? "",
                _ => ""
            };

            _colorPicker.ColorSelected -= OnColorPickerSelected;
            _colorPicker.ColorSelected += OnColorPickerSelected;
            _colorPicker.Open(swatch, currentColor);
        }
    }

    private void OnColorPickerSelected(object? sender, string hex)
    {
        switch (_activeColorField)
        {
            case "PropBgColor":
                PropBgColor.Text = hex;
                break;
            case "PropTextColor":
                PropTextColor.Text = hex;
                break;
            case "PropBorderColor":
                PropBorderColor.Text = hex;
                break;
            case "IndicatorColor":
                IndicatorColorBox.Text = hex;
                break;
            case "VsBgColor":
                VsBgColor.Text = hex;
                UpdateVisualStateSwatches();
                SaveVisualState(_currentControl!, _currentEditingState);
                break;
            case "VsTextColor":
                VsTextColor.Text = hex;
                UpdateVisualStateSwatches();
                SaveVisualState(_currentControl!, _currentEditingState);
                break;
            case "VsBorderColor":
                VsBorderColor.Text = hex;
                UpdateVisualStateSwatches();
                SaveVisualState(_currentControl!, _currentEditingState);
                break;
            case "ScreenBgColor":
                ScreenBgColor.Text = hex;
                UpdateScreenSwatches();
                break;
        }

        UpdateColorSwatches();
        _activeColorField = null;

        // 触发属性变更
        if (_currentControl != null)
            OnPropertyChanged(null, null!);
        else if (CurrentScreen != null)
            SaveScreenProperties();
    }

    private void UpdateColorSwatches()
    {
        try
        {
            if (!string.IsNullOrEmpty(PropBgColor.Text))
            {
                var hex = PropBgColor.Text.StartsWith('#') ? PropBgColor.Text : "#" + PropBgColor.Text;
                SwatchBgColor.Background = new SolidColorBrush(Color.Parse(hex));
            }

            if (!string.IsNullOrEmpty(PropTextColor.Text))
            {
                var hex = PropTextColor.Text.StartsWith('#') ? PropTextColor.Text : "#" + PropTextColor.Text;
                SwatchTextColor.Background = new SolidColorBrush(Color.Parse(hex));
            }

            if (!string.IsNullOrEmpty(PropBorderColor.Text))
            {
                var hex = PropBorderColor.Text.StartsWith('#') ? PropBorderColor.Text : "#" + PropBorderColor.Text;
                SwatchBorderColor.Background = new SolidColorBrush(Color.Parse(hex));
            }

            if (!string.IsNullOrEmpty(IndicatorColorBox.Text))
            {
                var hex = IndicatorColorBox.Text.StartsWith('#') ? IndicatorColorBox.Text : "#" + IndicatorColorBox.Text;
                SwatchIndicatorColor.Background = new SolidColorBrush(Color.Parse(hex));
            }
        }
        catch
        {
            // 忽略无效颜色值
        }
    }
}

namespace HmiEditor.App.Views;

using System;
using Avalonia.Controls;
using HmiEditor.Core.Design;
using HmiEditor.Core.Models;

/// <summary>
/// 「内容与文字 → 外观样式」这一组的写回：底色 / 边框 / 圆角 / 文字色 / 字号 / 对齐 /
/// 不透明度 / 文字排版 / 开关三色 / 曲线六色。
///
/// <para><b>闸门是行自己</b>：组里每一行都按控件类型显隐，隐藏行里的编辑器留着上一次
/// 选中控件的值 —— 无条件写回会把它灌进新控件（旋转 90° 的 Label → 切到表格改个名字，
/// 表格也被转 90°，登记 #61r 那一类）。</para>
///
/// <para>单独成文件：<c>PropertyPanelView.*.cs</c> 有 500 行上限
/// （<c>SingleFileSizeGuardTests</c>），而 <c>Changes.cs</c> 已经贴着上限。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>外观样式组按行写回。</summary>
    private void SaveAppearanceRows()
    {
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
        if (RowRadius.IsVisible)
            ValidateAndWrite(nameof(HmiControl.Radius), PropertyValidator.ClampRadius((int)PropRadius.Value), v => _currentControl.Radius = v);
        if (RowBorderWidth.IsVisible)
            ValidateAndWrite(nameof(HmiControl.BorderWidth), PropertyValidator.ClampBorderWidth((int)PropBorderWidth.Value), v => _currentControl.BorderWidth = v);

        // 字体 & 对齐
        if (RowFontSize.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FontSize), (int)PropFontSize.Value, v => _currentControl.FontSize = v);
        if (RowTextAlign.IsVisible && PropTextAlign.SelectedItem is ComboBoxItem ci_h && ci_h.Tag is string tagH)
            ValidateAndWrite(nameof(HmiControl.TextAlign), Enum.Parse<TextAlignMode>(tagH), v => _currentControl.TextAlign = v);
        if (RowTextVAlign.IsVisible && PropTextVAlign.SelectedItem is ComboBoxItem ci_v && ci_v.Tag is string tagV)
            ValidateAndWrite(nameof(HmiControl.TextVAlign), Enum.Parse<TextVAlignMode>(tagV), v => _currentControl.TextVAlign = v);
        // 字体族：面板恒隐藏（导出器不写 FontFamily），只在可见时写
        if (RowFontFamily.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FontFamily), PropFontFamily.SelectedItem is ComboBoxItem ci_f ? (ci_f.Tag as string) : null, v => _currentControl.FontFamily = v);
        // 不透明度（通用，0..255）
        if (RowOpacity.IsVisible)
            ValidateAndWrite(nameof(HmiControl.Opacity), OpacityBox.Value, v =>
                _currentControl.Opacity = (double)v!);

        // 文字排版（字形 / 字距 / 行距 / 超长处理）：只有 label 有这几条键
        if (RowLabelFontStyle.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelFontStyle), GetComboTag(LabelFontStyleCombo), v => _currentControl.LabelFontStyle = v);
        if (RowLetterSpacing.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelLetterSpacing), (int)LetterSpacingBox.Value, v => _currentControl.LabelLetterSpacing = v);
        if (RowLineSpacing.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelLineSpacing), (int)LineSpacingBox.Value, v => _currentControl.LabelLineSpacing = v);
        if (RowLongMode.IsVisible)
            ValidateAndWrite(nameof(HmiControl.LabelLongMode), GetComboTag(LongModeCombo), v => _currentControl.LabelLongMode = v);

        // 开关三段颜色（color_on / color_off / knob_color）
        if (RowSwitchColorOn.IsVisible)
            ValidateAndWrite(nameof(HmiControl.SwitchColorOn), string.IsNullOrWhiteSpace(SwitchColorOnBox.Text) ? null : SwitchColorOnBox.Text.Trim(), v => _currentControl.SwitchColorOn = v);
        if (RowSwitchColorOff.IsVisible)
            ValidateAndWrite(nameof(HmiControl.SwitchColorOff), string.IsNullOrWhiteSpace(SwitchColorOffBox.Text) ? null : SwitchColorOffBox.Text.Trim(), v => _currentControl.SwitchColorOff = v);
        if (RowSwitchKnob.IsVisible)
            ValidateAndWrite(nameof(HmiControl.SwitchKnobColor), string.IsNullOrWhiteSpace(SwitchKnobColorBox.Text) ? null : SwitchKnobColorBox.Text.Trim(), v => _currentControl.SwitchKnobColor = v);

        // 曲线的六个颜色（轴线 / 底色 / 外框 / 网格 / 刻度 / 标签）
        if (RowCurveAxisColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.CurveAxisColor), string.IsNullOrWhiteSpace(CurveAxisColorBox.Text) ? null : CurveAxisColorBox.Text.Trim(), v => _currentControl.CurveAxisColor = v);
        if (RowCurveBgColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.CurveBgColor), string.IsNullOrWhiteSpace(CurveBgColorBox.Text) ? null : CurveBgColorBox.Text.Trim(), v => _currentControl.CurveBgColor = v);
        if (RowCurveBorderColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.CurveBorderColor), string.IsNullOrWhiteSpace(CurveBorderColorBox.Text) ? null : CurveBorderColorBox.Text.Trim(), v => _currentControl.CurveBorderColor = v);
        if (RowCurveGridColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.CurveGridColor), string.IsNullOrWhiteSpace(CurveGridColorBox.Text) ? null : CurveGridColorBox.Text.Trim(), v => _currentControl.CurveGridColor = v);
        if (RowCurveTickColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.CurveTickColor), string.IsNullOrWhiteSpace(CurveTickColorBox.Text) ? null : CurveTickColorBox.Text.Trim(), v => _currentControl.CurveTickColor = v);
        if (RowCurveLabelColor.IsVisible)
            ValidateAndWrite(nameof(HmiControl.CurveLabelColor), string.IsNullOrWhiteSpace(CurveLabelColorBox.Text) ? null : CurveLabelColorBox.Text.Trim(), v => _currentControl.CurveLabelColor = v);
    }
}

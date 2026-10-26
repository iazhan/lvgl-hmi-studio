namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 图片族（值换图 / 帧动画 / 二维码）的写回。行定义见
/// <c>PropertyPanelView.Parameters.Media.cs</c>；拆文件的唯一原因是行数上限。
/// 空白一律写 null —— 模型里 null 与 "" 语义不同（null = 未配置）。
/// </summary>
public partial class PropertyPanelView
{
    private void WriteMediaFields()
    {
        if (StateImagesBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.StateImages), string.IsNullOrWhiteSpace(StateImagesBox.Text) ? null : StateImagesBox.Text.Trim(), v => _currentControl.StateImages = v);

        if (FrameImagesBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FrameImages), string.IsNullOrWhiteSpace(FrameImagesBox.Text) ? null : FrameImagesBox.Text.Trim(), v => _currentControl.FrameImages = v);
        if (FrameIntervalBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FrameIntervalMs), (int)FrameIntervalBox.Value, v => _currentControl.FrameIntervalMs = v);
        if (FrameLoopCheck.IsVisible)
            ValidateAndWrite(nameof(HmiControl.FrameLoop), FrameLoopCheck.IsChecked == true, v => _currentControl.FrameLoop = v);

        if (QrTextBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.QrText), string.IsNullOrWhiteSpace(QrTextBox.Text) ? null : QrTextBox.Text.Trim(), v => _currentControl.QrText = v);
        if (QrDarkColorBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.QrDarkColor), string.IsNullOrWhiteSpace(QrDarkColorBox.Text) ? null : QrDarkColorBox.Text.Trim(), v => _currentControl.QrDarkColor = v);
        if (QrLightColorBox.IsVisible)
            ValidateAndWrite(nameof(HmiControl.QrLightColor), string.IsNullOrWhiteSpace(QrLightColorBox.Text) ? null : QrLightColorBox.Text.Trim(), v => _currentControl.QrLightColor = v);
    }
}

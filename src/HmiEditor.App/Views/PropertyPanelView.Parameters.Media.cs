namespace HmiEditor.App.Views;

using HmiEditor.Core.Models;

/// <summary>
/// 属性面板里**图片族**的行：值换图（state_image）/ 帧动画（frame_anim）/ 二维码（qrcode）。
///
/// <para>这三类此前在面板上一行都没有 —— 模型字段齐全、导出器也会读，但用户改不到；
/// 其中值换图与帧动画缺的正是必填项，拖出来就"至少要有一项 / 没有可用的帧图片"、
/// 整份导出被 Error 中止。补齐后这几类才真的可用。</para>
///
/// <para>拆成独立分部文件的唯一原因是 <c>PropertyPanelView.*.cs</c> 有 500 行上限
/// （见 <c>SingleFileSizeGuardTests</c>），<c>Parameters.cs</c> 已接近上限。</para>
/// </summary>
public partial class PropertyPanelView
{
    /// <summary>图片族的三组行一起显隐（进面板先整块关掉）。</summary>
    private void ShowMediaRows(bool visible)
    {
        SetParamRow(LblStateImages, StateImagesBox, visible);
        SetParamRow(LblFrameImages, FrameImagesBox, visible);
        SetParamRow(LblFrameInterval, FrameIntervalBox, visible);
        SetParamRow(LblFrameLoop, FrameLoopCheck, visible);
        SetParamRow(LblQrText, QrTextBox, visible);
        SetParamRow(LblQrDark, QrDarkColorBox, visible);
        SetParamRow(LblQrLight, QrLightColorBox, visible);
    }

    /// <summary>打开该类型自己的行；不是图片族则什么都不做。</summary>
    private void ShowMediaRowsFor(HmiControlType type)
    {
        switch (type)
        {
            case HmiControlType.StateImage:
                SetParamRow(LblStateImages, StateImagesBox, true);
                break;
            case HmiControlType.FrameAnim:
                SetParamRow(LblFrameImages, FrameImagesBox, true);
                SetParamRow(LblFrameInterval, FrameIntervalBox, true);
                SetParamRow(LblFrameLoop, FrameLoopCheck, true);
                break;
            case HmiControlType.QrCode:
                SetParamRow(LblQrText, QrTextBox, true);
                SetParamRow(LblQrDark, QrDarkColorBox, true);
                SetParamRow(LblQrLight, QrLightColorBox, true);
                break;
        }
    }

    /// <summary>把图片族的字段回填到面板。</summary>
    private void FillMediaFields(HmiControl control)
    {
        StateImagesBox.Text = control.StateImages ?? "";

        FrameImagesBox.Text = control.FrameImages ?? "";
        FrameIntervalBox.Value = control.FrameIntervalMs;
        FrameLoopCheck.IsChecked = control.FrameLoop;

        QrTextBox.Text = control.QrText ?? "";
        QrDarkColorBox.Text = control.QrDarkColor ?? "";
        QrLightColorBox.Text = control.QrLightColor ?? "";
    }
}

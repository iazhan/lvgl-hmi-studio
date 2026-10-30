using System;
using HmiEditor.Core.Diagnostics;

namespace HmiEditor.App.Services;

/// <summary>
/// 编辑器诊断出口：终端 + 文件（<see cref="AppLog"/> 的 FileLogger）。
///
/// <para><b>为什么要落盘</b>：编辑器是 WinExe，Windows 上从资源管理器启动时没有控制台，
/// <c>Console.WriteLine</c> 写进一个不存在的句柄 —— "宿主给不出帧""页面 JSON 被拒"
/// 这类唯一能定位画布的诊断会整条消失。同一个方法在 Linux 终端启动时照旧看得见。</para>
///
/// <para>和 <see cref="AppLog"/> 的分工：那个是"异常/告警"通道；这里承载**过程**诊断
/// （[design] / 宿主 stderr），量级只有每页几次，可以常开。</para>
/// </summary>
public static class EditorLog
{
    /// <summary>诊断行：终端可读（Linux）+ 落盘（两个平台）。</summary>
    public static void Line(string message)
    {
        Console.WriteLine(message);
        AppLog.Info(message);
    }

    /// <summary>只落盘不刷终端 —— 转发宿主 stderr 这类"终端里本来就有"的重复内容用它。</summary>
    public static void File(string message) => AppLog.Info(message);
}

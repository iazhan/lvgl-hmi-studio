using System;
using System.Collections.Generic;
using System.IO;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// 导出产物的落点：目录或内存。
///
/// <para><b>为什么要这一层</b>：预览要的是"编辑器内存里的模型"，不该先写临时目录再让运行时读盘
/// （多一次 IO，也多一堆"路径含中文 / 只读安装目录"的失败面）。序列化本身只产字节，
/// 落盘只是其中一种消费方式 —— 用 sink 把两者分开，导出逻辑就只有一份。</para>
/// </summary>
public interface IChengeExportSink
{
    /// <summary>写进结果里的"输出位置"（目录路径，或内存模式的占位说明）。</summary>
    string Description { get; }

    /// <summary>开始写之前的一次性准备（目录 sink 在这里清残留、建目录）。</summary>
    void Begin();

    /// <summary>写一个文件。relative 是相对工程根的路径（"pages/home.json"）。</summary>
    void Write(string relative, byte[] bytes);
}

/// <summary>写进目录：正式导出、以及"导出并预览"起进程时用。</summary>
public sealed class DirectoryExportSink : IChengeExportSink
{
    private readonly string _dir;

    public DirectoryExportSink(string dir) => _dir = dir;

    public string Description => _dir;

    public void Begin()
    {
        Directory.CreateDirectory(_dir);

        // 清掉上一次导出的残留页面/资产：工程目录是运行时直接读的，
        // 残留页面会出现在页面栈里（旧页 id 被新工程引用时就错乱了）。
        foreach (var stale in new[] { Path.Combine(_dir, "pages"), Path.Combine(_dir, "assets") })
        {
            if (Directory.Exists(stale)) Directory.Delete(stale, recursive: true);
        }
    }

    public void Write(string relative, byte[] bytes)
    {
        var path = Path.Combine(_dir, relative);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>写进内存：预览用，不落盘。</summary>
public sealed class MemoryExportSink : IChengeExportSink
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public string Description => "(memory)";

    public void Begin()
    {
    }

    public void Write(string relative, byte[] bytes) => Files[relative] = bytes;
}

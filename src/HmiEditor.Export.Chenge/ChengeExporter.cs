using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HmiEditor.Core.Models;
using HmiEditor.Core.Models.Alarm;
using HmiEditor.Core.Models.Trend;
using HmiEditor.Core.Models.User;

namespace HmiEditor.Export.Chenge;

/// <summary>
/// `HmiProject` → chenge-runtime 37 工程目录。
///
/// <para><b>确定性</b>：同输入同输出（id 按变量/页面/控件的既有顺序分配，不掺时间戳），
/// 否则 golden 回归与"导出两次比对"都做不了。</para>
///
/// <para><b>先算后写</b>：所有文件先在内存里生成并完成校验，只要有一条 Error 就一个字都不落盘——
/// 半份工程比没有工程更难查（运行时会拿它跑起来，然后在不相关的地方报错）。</para>
/// </summary>
public sealed class ChengeExporter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ChengeSchema _schema;

    public ChengeExporter(ChengeSchema? schema = null) => _schema = schema ?? ChengeSchema.Load();

    public ChengeExportResult Export(HmiProject project, AssetLibrary? assets, ChengeExportOptions options,
        string outputDir, string? projectDirectory = null)
        => ExportCore(project, assets, options, projectDirectory, new DirectoryExportSink(outputDir));

    /// <summary>
    /// 导出到内存：预览用（运行时直接吃字节，不落盘）。返回 相对路径 → 文件内容；
    /// 有错误时 <paramref name="result"/> 里带问题清单，字典可能不完整。
    /// </summary>
    public Dictionary<string, byte[]> ExportToMemory(HmiProject project, AssetLibrary? assets,
        ChengeExportOptions options, string? projectDirectory, out ChengeExportResult result)
    {
        var sink = new MemoryExportSink();

        result = ExportCore(project, assets, options, projectDirectory, sink);
        return sink.Files;
    }

    private ChengeExportResult ExportCore(HmiProject project, AssetLibrary? assets, ChengeExportOptions options,
        string? projectDirectory, IChengeExportSink sink)
    {
        var issues = new ChengeIssueBag();
        var colors = new ChengeColors(project.Theme, issues);
        var assetStore = new ChengeAssets(assets, projectDirectory, issues);
        var maps = new ChengeMaps();
        var features = new HashSet<string>(StringComparer.Ordinal);
        var fontSizes = new HashSet<int>();
        var files = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        foreach (var script in project.Scripts) maps.ScriptById[script.Id] = script;

        var screens = project.Screens.ToList();
        var (screenWidth, screenHeight) = ResolveScreen(screens, options);
        BuildPages(maps, screens, issues);

        var ports = new JsonArray();
        var devices = new JsonArray();
        BuildDevices(project, maps, ports, devices, features, issues);

        var tags = BuildTags(project, maps, issues);

        var mapper = new ChengeWidgetMapper(_schema, issues, colors, assetStore, maps, features, screenWidth, screenHeight);
        var pageDocs = new List<(string Path, JsonNode Node)>();
        var widgetCount = 0;
        foreach (var screen in screens)
        {
            var pageId = maps.PageIdByScreenId[screen.Id];
            var doc = BuildPage(project, screen, pageId, maps, mapper, colors, assetStore, features, fontSizes, issues, ref widgetCount);
            pageDocs.Add(($"pages/{pageId}.json", doc));
        }

        files["ports.json"] = ports;
        files["devices.json"] = devices;
        files["tags.json"] = tags;

        var startPage = options.StartPageId;
        if (string.IsNullOrWhiteSpace(startPage) || !maps.PageIdByScreenId.ContainsValue(startPage))
        {
            var first = screens.FirstOrDefault();
            startPage = first != null ? maps.PageIdByScreenId[first.Id] : "";
        }
        if (string.IsNullOrEmpty(startPage))
            issues.Error("manifest.no_pages", "manifest.json", "工程里没有任何页面，无法导出");

        var strings = BuildStrings(project, issues);
        if (strings != null)
        {
            files["strings.json"] = strings;
            features.Add("i18n");
        }
        var alarmDoc = BuildAlarms(project, maps, issues);
        if (alarmDoc != null)
        {
            files["alarms.json"] = alarmDoc;
            features.Add("alarm");
        }
        var savecfg = BuildSaveCfg(project, maps, issues);
        if (savecfg != null) files["savecfg.json"] = savecfg;
        var users = BuildUsers(project, features, issues);
        if (users != null)
        {
            files["users.json"] = users;
            features.Add("users");
        }

        var hwProfile = ResolveHwProfile(project, options, issues);
        var manifest = new JsonObject
        {
            ["format_version"] = "0.6",
            ["min_runtime"] = "0.1.0",
            ["hw_profile"] = hwProfile,
            ["name"] = string.IsNullOrWhiteSpace(project.Name) ? "chenge_project" : project.Name,
            ["version"] = string.IsNullOrWhiteSpace(project.ProjectVersion) ? "1.0.0" : project.ProjectVersion,
            ["screen"] = new JsonObject { ["width"] = screenWidth, ["height"] = screenHeight },
            ["start_page"] = startPage ?? "",
            ["default_language"] = options.DefaultLanguage,
            ["features"] = FeatureArray(features),
            ["font_sizes"] = new JsonArray(fontSizes.OrderBy(x => x).Select(x => (JsonNode)x).ToArray()),
            ["counts"] = new JsonObject
            {
                ["tags"] = tags.Count,
                ["pages"] = pageDocs.Count,
                ["devices"] = devices.Count,
                ["scripts"] = 0,
            },
        };
        if (features.Contains("lua")) manifest["lua_api"] = 1;

        foreach (var (path, node) in pageDocs) files[path] = node;

        var result = new ChengeExportResult
        {
            Issues = issues,
            OutputDirectory = sink.Description,
            PageCount = pageDocs.Count,
            WidgetCount = widgetCount,
            TagCount = tags.Count,
            DeviceCount = devices.Count,
        };

        if (issues.HasErrors) return result;

        var written = WriteAll(sink, manifest, files, assetStore, issues);
        return new ChengeExportResult
        {
            Issues = issues,
            OutputDirectory = sink.Description,
            PageCount = pageDocs.Count,
            WidgetCount = widgetCount,
            TagCount = tags.Count,
            DeviceCount = devices.Count,
            WrittenFiles = written,
        };
    }

    // ── 页面 ──

    private void BuildPages(ChengeMaps maps, IReadOnlyList<HmiScreen> screens, ChengeIssueBag issues)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in screens)
        {
            var slug = ChengeNames.Slug(screen.Name, "page");
            var id = ChengeNames.Unique(slug, used);
            if (slug != id)
                issues.Warn("page.id_collision", $"screen:{screen.Name}",
                    $"页面 id {slug} 已被占用，改用 {id}");
            maps.PageIdByScreenId[screen.Id] = id;
            maps.PageIdByScreenName[screen.Name] = id;
        }
    }

    private JsonObject BuildPage(HmiProject project, HmiScreen screen, string pageId, ChengeMaps maps,
        ChengeWidgetMapper mapper, ChengeColors colors, ChengeAssets assets, HashSet<string> features,
        HashSet<int> fontSizes, ChengeIssueBag issues, ref int widgetCount)
    {
        var page = new JsonObject
        {
            ["id"] = pageId,
            ["title"] = screen.Name,
        };
        page["bg_color"] = colors.Resolve(screen.BgColor, pageId) ?? colors.Background;

        var bgSrc = assets.SrcFor(screen.BgImageAssetId, pageId);
        if (bgSrc != null)
        {
            page["bg_image"] = new JsonObject
            {
                ["src"] = bgSrc,
                ["mode"] = screen.BgImageMode switch
                {
                    ScreenBgImageMode.Tile => "tile",
                    ScreenBgImageMode.Center => "center",
                    _ => "stretch",
                },
                ["opacity"] = Math.Clamp((int)Math.Round(screen.BgImageOpacity * 255 / 100.0), 0, 255),
            };
        }

        var layers = new JsonArray();
        foreach (var layerId in screen.ReferencedLayerIds)
        {
            var layer = project.Screens.FirstOrDefault(s => s.Id == layerId);
            if (layer == null)
            {
                issues.Error("page.layer_missing", pageId, $"图层引用的屏幕 {layerId} 不存在");
                continue;
            }
            layers.Add(maps.PageIdByScreenId[layer.Id]);
        }
        if (layers.Count > 0) page["layers"] = layers;

        var widgets = new JsonArray();
        var seq = 0;
        foreach (var control in screen.Controls.Where(c => string.IsNullOrEmpty(c.ParentId)))
            AppendWidget(widgets, control, pageId, "w", ref seq, maps, mapper, features, fontSizes, issues, ref widgetCount);
        page["widgets"] = widgets;
        return page;
    }

    private void AppendWidget(JsonArray target, HmiControl control, string pageId, string idPrefix,
        ref int seq, ChengeMaps maps, ChengeWidgetMapper mapper, HashSet<string> features,
        HashSet<int> fontSizes, ChengeIssueBag issues, ref int widgetCount)
    {
        seq++;
        var widgetId = idPrefix + seq;
        if (control.Children.Count > 0)
        {
            var container = new JsonObject
            {
                ["id"] = widgetId,
                ["type"] = "shape",
                ["x"] = (int)Math.Round(control.X),
                ["y"] = (int)Math.Round(control.Y),
                ["w"] = (int)Math.Round(Math.Max(1, control.Width)),
                ["h"] = (int)Math.Round(Math.Max(1, control.Height)),
                ["props"] = new JsonObject { ["kind"] = "container" },
            };
            var children = new JsonArray();
            var childSeq = 0;
            foreach (var child in control.Children)
                AppendWidget(children, child, pageId, widgetId + "_", ref childSeq, maps, mapper, features, fontSizes, issues, ref widgetCount);
            container["props"]!["children"] = children;
            target.Add(container);
            widgetCount++;
            return;
        }

        var widget = mapper.Map(control, pageId, widgetId);
        if (widget == null) return;
        target.Add(widget);
        widgetCount++;
        fontsAdd(fontSizes, control);
    }

    private static void fontsAdd(HashSet<int> fontSizes, HmiControl control)
    {
        if (control.FontSize > 0) fontSizes.Add(control.FontSize);
        foreach (var child in control.Children) fontsAdd(fontSizes, child);
    }

    // ── tags ──

    private JsonArray BuildTags(HmiProject project, ChengeMaps maps, ChengeIssueBag issues)
    {
        var tags = new JsonArray();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var nextId = 0;
        var slaveDeviceByVarId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var variable in project.VariableManager.Variables)
        {
            if (variable.IsUdtInstance)
            {
                issues.Warn("tag.udt", $"variable:{variable.Name}",
                    "UDT 实例暂不支持导出（37 的 tags.json 没有结构体）；请展开成独立变量");
                continue;
            }
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                issues.Error("tag.no_name", "tags.json", $"变量 {variable.Id} 没有名字");
                continue;
            }
            if (!usedNames.Add(variable.Name))
            {
                issues.Error("tag.duplicate_name", "tags.json", $"变量名 {variable.Name} 重复（37：工程内唯一）");
                continue;
            }

            var id = nextId++;
            maps.TagByVariableId[variable.Id] = id;
            maps.TagByVariableName[variable.Name] = id;
            maps.NameByTag[id] = variable.Name;

            var type = MapDataType(variable.DataType);
            if (type == null)
            {
                issues.Error("tag.type", $"variable:{variable.Name}", $"数据类型 {variable.DataType} 无对应（i16/u16/i32/u32/f32/i64/double/string/bool）");
                type = "i32";
            }
            maps.TypeByTag[id] = type;

            var tag = new JsonObject
            {
                ["id"] = id,
                ["name"] = variable.Name,
                ["type"] = type,
            };

            var access = string.IsNullOrWhiteSpace(variable.Access) ? "rw" : variable.Access.Trim().ToLowerInvariant();
            if (variable.Area is "input" or "discrete" && access == "rw")
            {
                issues.Warn("tag.readonly_area", $"variable:{variable.Name}",
                    $"{variable.Area} 是只读区，access 已按 r 导出");
                access = "r";
            }
            tag["access"] = access == "r" ? "r" : "rw";

            if (!string.IsNullOrWhiteSpace(variable.Unit)) tag["unit"] = variable.Unit;
            if (type == "string") tag["len"] = Math.Clamp(variable.StringLen <= 0 ? 16 : variable.StringLen, 1, 64);

            var hasSource = !string.IsNullOrWhiteSpace(variable.ConnectionId);
            if (hasSource)
            {
                var deviceId = ResolveDeviceId(variable, project, maps, issues);
                if (deviceId == null)
                {
                    issues.Error("tag.device", $"variable:{variable.Name}",
                        $"变量的设备连接 {variable.ConnectionId} 没有出现在 devices.json 里");
                }
                else
                {
                    var source = new JsonObject
                    {
                        ["device"] = deviceId,
                        ["datatype"] = type == "string" ? "string" : type,
                    };
                    var area = string.IsNullOrWhiteSpace(variable.Area) ? "holding" : variable.Area.Trim().ToLowerInvariant();
                    if (area is not ("holding" or "coil" or "input" or "discrete" or "db" or "mk" or "pe" or "pa"))
                    {
                        issues.Error("tag.area", $"variable:{variable.Name}", $"寄存器区 {area} 非法");
                        area = "holding";
                    }
                    source["area"] = area;
                    var addr = ParseAddress(variable.Address, area, issues, variable.Name);
                    source["addr"] = addr;
                    if (area is "coil" or "discrete") source["datatype"] = "bool";
                    if (area is "coil" or "discrete" && type != "bool")
                        issues.Error("tag.coil_type", $"variable:{variable.Name}", $"{area} 区的变量类型必须是 bool");
                    if (!string.IsNullOrWhiteSpace(variable.ByteOrder)) source["order"] = variable.ByteOrder!.Trim().ToUpperInvariant();
                    if (type == "string" && !string.IsNullOrWhiteSpace(variable.Encoding)) source["encoding"] = variable.Encoding;
                    if (variable.Bit is >= 0 and <= 15)
                    {
                        source["bit"] = variable.Bit.Value;
                        if (type != "bool")
                            issues.Error("tag.bit_type", $"variable:{variable.Name}", "bit 只对 bool 变量有效（37 §5）");
                    }
                    if (Math.Abs(variable.Scale - 1.0) > 1e-9) source["scale"] = variable.Scale;
                    if (Math.Abs(variable.Offset) > 1e-9) source["offset"] = variable.Offset;
                    tag["source"] = source;
                }
            }
            else
            {
                if (type is "f32" or "double" or "i64")
                {
                    if (double.TryParse(variable.SimulatedValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var init))
                        tag["initial"] = init;
                }
                else if (type != "string" && int.TryParse(variable.SimulatedValue, out var initInt))
                {
                    tag["initial"] = initInt;
                }
            }

            if ((Math.Abs(variable.Scale - 1.0) > 1e-9 || Math.Abs(variable.Offset) > 1e-9) && type is not ("f32" or "double"))
                issues.Error("tag.scale_type", $"variable:{variable.Name}",
                    "scale/offset 非默认值时类型必须是 f32 或 double（37 §5）");

            tags.Add(tag);
        }

        MapSystemVariables(maps);
        return tags;
    }

    private static string? ResolveDeviceId(Variable variable, HmiProject project, ChengeMaps maps, ChengeIssueBag issues)
    {
        if (!string.IsNullOrWhiteSpace(variable.SlaveId) && maps.SlaveDeviceIdBySlaveId.TryGetValue(variable.SlaveId, out var slaveDevice))
            return slaveDevice;
        if (maps.DeviceIdByConnectionId.TryGetValue(variable.ConnectionId, out var byConn)) return byConn;
        return null;
    }

    private int ParseAddress(string? address, string area, ChengeIssueBag issues, string variableName)
    {
        var raw = (address ?? "").Trim();
        if (raw.Length == 0)
        {
            issues.Warn("tag.no_address", $"variable:{variableName}", "变量没有地址，按 0 导出（请确认）");
            return 0;
        }

        // Modbus 5 位寻址（4xxxx）与 "holding:100" / "D100" 两种工程里都见过的写法
        if (raw.Contains(':'))
        {
            var parts = raw.Split(':', 2);
            if (int.TryParse(parts[1], out var v)) return Math.Max(0, v);
        }
        if (raw.Length == 5 && char.IsDigit(raw[0]))
        {
            var offset = int.TryParse(raw[1..], out var o) ? o : 0;
            return Math.Max(0, offset - 1);
        }
        if ((raw[0] is 'D' or 'd' or 'M' or 'R') && int.TryParse(raw[1..], out var mcgs))
            return Math.Max(0, mcgs - 1);
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;
        if (int.TryParse(raw, out var plain)) return Math.Max(0, plain - (plain > 40000 ? 40001 : 0));
        issues.Warn("tag.address_unparsed", $"variable:{variableName}", $"地址 {raw} 无法解析，按 0 导出");
        return 0;
    }

    private void MapSystemVariables(ChengeMaps maps)
    {
        var ids = _schema.SystemVariableIds;
        void Map(string editorId, string runtimeName)
        {
            if (ids.TryGetValue(runtimeName, out var id)) maps.SysTagByEditorId[editorId] = id;
        }
        Map("sys.time.cur", "sys.time");
        Map("sys.time.date", "sys.date");
        Map("sys.time.uptime", "sys.uptime");
        Map("sys.user.name", "sys.user");
        foreach (var pair in new (string Editor, string Name)[]
                 {
                     ("sys.time.datetime", "sys.date"),
                 })
            Map(pair.Editor, pair.Name);
    }

    private static string? MapDataType(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "bool" or "boolean" => "bool",
        "int16" or "short" or "i16" => "i16",
        "uint16" or "ushort" or "u16" => "u16",
        "int32" or "int" or "i32" => "i32",
        "uint32" or "uint" or "u32" => "u32",
        "float" or "single" or "f32" => "f32",
        "double" or "float64" => "double",
        "int64" or "long" or "i64" => "i64",
        "string" or "str" => "string",
        _ => null,
    };

    // ── 设备与串口 ──

    private void BuildDevices(HmiProject project, ChengeMaps maps, JsonArray ports, JsonArray devices,
        HashSet<string> features, ChengeIssueBag issues)
    {
        var usedPorts = new HashSet<string>(StringComparer.Ordinal);
        var portDeviceCount = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var conn in project.VariableManager.Connections.Where(c => c.IsEnabled))
        {
            var driver = (conn.DriverId ?? "").ToLowerInvariant();
            var isRtu = driver.Contains("modbus") && driver.Contains("rtu");
            var isTcp = driver.Contains("modbus") && (driver.Contains("tcp") || driver.Contains("ip"));

            if (!isRtu && !isTcp)
            {
                issues.Error("device.protocol", $"device:{conn.Name}",
                    $"协议 {conn.DriverId} 在本分支不支持（单轨只到 chenge-runtime 的 modbus_rtu / modbus_tcp；mqtt / opcua / s7 见 MAPPING-REPORT）");
                continue;
            }

            var connDeviceId = ChengeNames.Slug(conn.Name, "dev");
            if (isTcp)
            {
                features.Add("modbus_tcp");
                var host = GetConfig(conn, "host", "ip", "address");
                var tcpPort = ParseInt(GetConfig(conn, "tcpPort", "port", "tcp_port"), 502);
                if (string.IsNullOrWhiteSpace(host))
                {
                    issues.Error("device.tcp_host", $"device:{conn.Name}", "Modbus TCP 设备缺少 host（37 §4）");
                    continue;
                }
                var dev = new JsonObject
                {
                    ["id"] = connDeviceId,
                    ["name"] = conn.Name,
                    ["protocol"] = "modbus_tcp",
                    ["host"] = host,
                    ["tcp_port"] = tcpPort,
                    ["slave"] = ParseInt(GetConfig(conn, "slaveId", "slave"), 1),
                    ["timeout_ms"] = ParseInt(GetConfig(conn, "timeoutMs", "timeout"), 1000),
                    ["retries"] = 1,
                    ["poll_ms"] = 500,
                    ["default_order"] = "ABCD",
                };
                devices.Add(dev);
                maps.DeviceIdByConnectionId[conn.Id] = connDeviceId;
                continue;
            }

            var portName = NormalizePort(GetConfig(conn, "port", "comPort"), issues, conn.Name);
            if (portName == null)
            {
                issues.Error("device.port", $"device:{conn.Name}", "Modbus RTU 设备缺少串口（37 §4：port 必须指向 ports.json 的串口 id）");
                continue;
            }
            if (usedPorts.Add(portName))
            {
                ports.Add(new JsonObject
                {
                    ["id"] = portName,
                    ["baud"] = ParseInt(GetConfig(conn, "baud"), 9600),
                    ["data_bits"] = ParseInt(GetConfig(conn, "dataBits"), 8),
                    ["parity"] = (GetConfig(conn, "parity") ?? "none").ToLowerInvariant(),
                    ["stop_bits"] = ParseInt(GetConfig(conn, "stopBits"), 1),
                });
            }
            portDeviceCount[portName] = portDeviceCount.GetValueOrDefault(portName) + 1;

            var timeout = ParseInt(GetConfig(conn, "timeoutMs", "timeout"), 300);
            var retries = ParseInt(GetConfig(conn, "retries"), 2);
            var poll = Math.Clamp(conn.Slaves.FirstOrDefault()?.Config.TryGetValue("pollMs", out var p) == true ? ParseInt(p, 200) : 200, 50, 60000);

            if (conn.Slaves.Count > 0)
            {
                foreach (var slave in conn.Slaves.Where(s => s.IsEnabled))
                {
                    var id = ChengeNames.Slug($"{conn.Name}_{slave.SlaveAddress}", "dev");
                    devices.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["name"] = string.IsNullOrWhiteSpace(slave.Name) ? $"{conn.Name} 站{slave.SlaveAddress}" : slave.Name,
                        ["protocol"] = "modbus_rtu",
                        ["port"] = portName,
                        ["slave"] = Math.Clamp(slave.SlaveAddress, 1, 247),
                        ["timeout_ms"] = timeout,
                        ["retries"] = retries,
                        ["poll_ms"] = poll,
                        ["default_order"] = "ABCD",
                    });
                    maps.SlaveDeviceIdBySlaveId[slave.Id] = id;
                }
                // 直挂连接（没走从站）的变量落到第一个从站
                var firstSlave = conn.Slaves.FirstOrDefault(s => s.IsEnabled);
                if (firstSlave != null && maps.SlaveDeviceIdBySlaveId.TryGetValue(firstSlave.Id, out var firstId))
                    maps.DeviceIdByConnectionId[conn.Id] = firstId;
            }
            else
            {
                devices.Add(new JsonObject
                {
                    ["id"] = connDeviceId,
                    ["name"] = conn.Name,
                    ["protocol"] = "modbus_rtu",
                    ["port"] = portName,
                    ["slave"] = Math.Clamp(ParseInt(GetConfig(conn, "slaveId", "slave"), 1), 1, 247),
                    ["timeout_ms"] = timeout,
                    ["retries"] = retries,
                    ["poll_ms"] = poll,
                    ["default_order"] = "ABCD",
                });
                maps.DeviceIdByConnectionId[conn.Id] = connDeviceId;
            }
        }

        if (portDeviceCount.TryGetValue("com2", out var com2) && com2 > 1)
            issues.Error("port.rs232_multi", "ports.json", $"com2 是 RS232，只允许挂 1 个设备，实际 {com2} 个（37 §4）");
    }

    private static string? NormalizePort(string? raw, ChengeIssueBag issues, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToLowerInvariant().Replace(" ", "");
        if (s.StartsWith("com") && int.TryParse(s[3..], out var n)) return "com" + n;
        issues.Warn("device.port_name", $"device:{deviceName}", $"串口名 {raw} 无法识别，按 com1 导出");
        return "com1";
    }

    private static string? GetConfig(DeviceConnection conn, params string[] keys)
    {
        foreach (var key in keys)
        {
            var hit = conn.Config.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(hit.Value)) return hit.Value;
        }
        return null;
    }

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // ── 报警 / 存盘 / 用户 / 字符串 ──

    private JsonObject? BuildAlarms(HmiProject project, ChengeMaps maps, ChengeIssueBag issues)
    {
        var alarms = new JsonArray();
        var nextId = 0;

        foreach (var group in project.AlarmGroups)
        {
            foreach (var alarm in group.Alarms.Where(a => a.IsEnabled))
            {
                var where = $"alarms.json:{group.Name}/{alarm.Name}";
                var tag = maps.ResolveTag(alarm.Condition.VariableId);
                if (tag == null)
                {
                    issues.Error("alarm.tag", where, $"报警引用的变量 {alarm.Condition.VariableId} 不存在");
                    continue;
                }

                var type = alarm.Condition.Type switch
                {
                    AlarmConditionType.HighLimit => "hi",
                    AlarmConditionType.HighHighLimit => "hi_hi",
                    AlarmConditionType.LowLimit => "lo",
                    AlarmConditionType.LowLowLimit => "lo_lo",
                    AlarmConditionType.BitTrigger => "bool",
                    AlarmConditionType.RateOfChange => "rate_of_change",
                    AlarmConditionType.Deviation => "deviation",
                    _ => null,
                };
                if (type == null)
                {
                    issues.Warn("alarm.condition", where,
                        $"报警条件 {alarm.Condition.Type} 没有对应（37：hi/hi_hi/lo/lo_lo/bool/comm/deviation/rate_of_change），已跳过");
                    continue;
                }
                if (type == "deviation")
                {
                    issues.Warn("alarm.deviation", where,
                        "偏差报警需要参考变量（ref_tag），编辑器只有常数期望值——目标契约要求 ref_tag 指向变量，本轮跳过");
                    continue;
                }

                var obj = new JsonObject
                {
                    ["id"] = nextId++,
                    ["tag"] = tag.Value,
                    ["type"] = type,
                };
                if (type is "hi" or "hi_hi" or "lo" or "lo_lo" or "rate_of_change")
                    obj["limit"] = alarm.Condition.Threshold;
                if (type == "rate_of_change")
                    obj["window_ms"] = Math.Clamp(alarm.Condition.DelayMs > 0 ? alarm.Condition.DelayMs : 2000, 200, 600000);
                if (alarm.Condition.Deadband != 0) obj["deadband"] = alarm.Condition.Deadband;
                if (alarm.Condition.DelayMs > 0 && type != "rate_of_change")
                    obj["delay_ms"] = Math.Clamp(alarm.Condition.DelayMs, 0, 60000);
                obj["level"] = alarm.Severity switch
                {
                    AlarmSeverity.Info => 1,
                    AlarmSeverity.Warning => 2,
                    AlarmSeverity.Critical => 3,
                    _ => 4,
                };
                obj["ack"] = alarm.RequiresAck;
                obj["group"] = group.Name;

                if (!string.IsNullOrWhiteSpace(alarm.TextId))
                {
                    obj["text_id"] = alarm.TextId;
                    _stringTexts[alarm.TextId!] = alarm.Description ?? alarm.Name;
                }
                else
                {
                    obj["text"] = alarm.Description is { Length: > 0 } d ? d : alarm.Name;
                }
                if (!string.IsNullOrWhiteSpace(alarm.TextClearId))
                {
                    obj["text_clear_id"] = alarm.TextClearId;
                    _stringTexts[alarm.TextClearId!] = alarm.TextClear ?? alarm.Name;
                }
                else if (!string.IsNullOrWhiteSpace(alarm.TextClear))
                {
                    obj["text_clear"] = alarm.TextClear;
                }

                alarms.Add(obj);
            }
        }

        // 变量内联报警（tags.json 的 alarms 与 alarms.json 等价；统一写进 alarms.json）
        foreach (var variable in project.VariableManager.Variables)
        {
            foreach (var inline in variable.Alarms.Where(a => a.IsEnabled))
            {
                var where = $"alarms.json:{variable.Name}";
                var tag = maps.ResolveTag(variable.Id);
                if (tag == null) continue;
                var type = inline.Type switch
                {
                    AlarmConditionType.HighLimit => "hi",
                    AlarmConditionType.HighHighLimit => "hi_hi",
                    AlarmConditionType.LowLimit => "lo",
                    AlarmConditionType.LowLowLimit => "lo_lo",
                    AlarmConditionType.BitTrigger => "bool",
                    AlarmConditionType.RateOfChange => "rate_of_change",
                    _ => null,
                };
                if (type == null)
                {
                    issues.Warn("alarm.inline_condition", where,
                        $"变量报警条件 {inline.Type} 没有对应（偏差/状态变化需要参考变量），已跳过");
                    continue;
                }
                var obj = new JsonObject
                {
                    ["id"] = nextId++,
                    ["tag"] = tag.Value,
                    ["type"] = type,
                    ["level"] = inline.Severity switch
                    {
                        AlarmSeverity.Info => 1,
                        AlarmSeverity.Warning => 2,
                        AlarmSeverity.Critical => 3,
                        _ => 4,
                    },
                    ["ack"] = inline.RequiresAck,
                    ["text"] = string.IsNullOrWhiteSpace(inline.Message) ? variable.Name + " 报警" : inline.Message,
                };
                if (type != "bool") obj["limit"] = inline.Threshold;
                if (inline.Deadband != 0) obj["deadband"] = inline.Deadband;
                if (inline.DelayMs > 0 && type != "rate_of_change")
                    obj["delay_ms"] = Math.Clamp(inline.DelayMs, 0, 60000);
                alarms.Add(obj);
            }
        }

        return alarms.Count == 0 ? null : new JsonObject { ["alarms"] = alarms };
    }

    private readonly Dictionary<string, string> _stringTexts = new(StringComparer.Ordinal);

    private JsonNode? BuildStrings(HmiProject project, ChengeIssueBag issues)
    {
        if (_stringTexts.Count == 0) return null;
        var texts = new JsonArray();
        foreach (var (id, text) in _stringTexts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z][a-z0-9_.-]{0,63}$"))
            {
                issues.Error("strings.id", "strings.json", $"文本 id {id} 不符合 [a-z][a-z0-9_.-]{{0,63}}");
                continue;
            }
            texts.Add(new JsonObject { ["id"] = id, ["zh"] = text });
        }
        return new JsonObject
        {
            ["default"] = "zh",
            ["languages"] = new JsonArray(new JsonObject { ["code"] = "zh", ["name"] = "简体中文" }),
            ["texts"] = texts,
        };
    }

    private JsonNode? BuildSaveCfg(HmiProject project, ChengeMaps maps, ChengeIssueBag issues)
    {
        var groups = new JsonArray();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var log in project.DataLogConfigs.Where(l => l.IsEnabled))
        {
            var name = ChengeNames.Slug(string.IsNullOrWhiteSpace(log.Name) ? "log" : log.Name, "log");
            name = ChengeNames.Unique(name, used);
            var tagIds = new JsonArray();
            var missing = 0;
            foreach (var varId in log.VariableIds)
            {
                var tag = maps.ResolveTag(varId);
                if (tag == null) { missing++; continue; }
                tagIds.Add(tag.Value);
            }
            if (missing > 0)
                issues.Warn("savecfg.tag_missing", $"savecfg.json:{name}", $"{missing} 个采集变量不存在，已跳过");

            if (tagIds.Count == 0)
            {
                issues.Warn("savecfg.no_tags", $"savecfg.json:{name}", "存盘组没有有效变量，已跳过（37：tags 1..32 个）");
                continue;
            }

            var group = new JsonObject
            {
                ["name"] = name,
                ["mode"] = log.Mode is "period" or "change" or "trigger" ? log.Mode : "period",
                ["tags"] = tagIds,
                ["max_mb"] = Math.Clamp(log.MaxMb <= 0 ? 4 : log.MaxMb, 1, 64),
                ["csv_delimiter"] = string.IsNullOrEmpty(log.CsvDelimiter) ? "," : log.CsvDelimiter,
                ["csv_time_format"] = log.CsvTimeFormat == "unix" ? "unix" : "iso",
                ["record_timestamp"] = log.RecordTimestamp,
            };
            if (group["mode"]!.GetValue<string>() == "period")
                group["period_s"] = Math.Clamp(log.PeriodS <= 0 ? 60 : log.PeriodS, 1, 3600);
            if (log.MaxRecords > 0)
                group["max_records"] = Math.Clamp(log.MaxRecords, 2, 200000);
            group["full_policy"] = log.FullPolicy == LogFullPolicy.StopWhenFull ? "stop" : "overwrite";
            if (!string.IsNullOrWhiteSpace(log.StoragePath))
            {
                var path = log.StoragePath!.Trim();
                if (path.StartsWith("/data/", StringComparison.Ordinal) || path.StartsWith("userdata", StringComparison.Ordinal))
                    group["storage_path"] = path;
                else
                    issues.Warn("savecfg.storage_path", $"savecfg.json:{name}",
                        $"存储路径 {path} 不在数据目录或 /data/userdata 之下，运行时会拒绝整份文件，已丢弃该字段");
            }
            groups.Add(group);
        }
        return groups.Count == 0 ? null : new JsonObject { ["groups"] = groups };
    }

    private JsonNode? BuildUsers(HmiProject project, HashSet<string> features, ChengeIssueBag issues)
    {
        var users = project.SecurityConfig.RuntimeUsers.Where(u => !string.IsNullOrWhiteSpace(u.Username)).ToList();
        if (users.Count == 0) return null;

        var hashed = users.Where(u => !string.IsNullOrWhiteSpace(u.PasswordHash)).ToList();
        if (hashed.Count > 0)
        {
            issues.Warn("users.password_format", "users.json",
                $"未导出 users.json：编辑器存的口令哈希是 Base64Salt:Base64Hash，契约要求 sha256$hex$hex（999 轮），不能互转；" +
                $"带口令的用户有 {hashed.Count} 个（{string.Join('、', hashed.Select(u => u.Username))}）。" +
                "清空这些口令后重新导出即可带上权限位（导出免密用户不会伪造凭据）");
            return null;
        }

        var perms = _schema.PermissionKeys;
        var groups = new JsonArray();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in UserRole.BuiltInAll)
        {
            var id = ChengeNames.Slug(role.Id, "grp");
            groupIds.Add(role.Id);
            var group = new JsonObject
            {
                ["id"] = id,
                ["name"] = role.Name,
                ["admin"] = role.Id == "role-admin",
            };
            var bits = new JsonArray();
            for (var bit = 0; bit < perms.Count; bit++)
            {
                if (((int)role.Permissions & (1 << bit)) != 0) bits.Add(perms[bit]);
            }
            group["perms"] = bits;
            groups.Add(group);
        }

        var userArr = new JsonArray();
        foreach (var u in users)
        {
            var roleId = string.IsNullOrWhiteSpace(u.RoleId) ? project.SecurityConfig.DefaultRoleId : u.RoleId;
            if (!groupIds.Contains(roleId))
            {
                issues.Warn("users.role", $"users.json:{u.Username}", $"角色 {roleId} 不是内置三角色之一，已按操作员导出");
                roleId = "role-operator";
            }
            userArr.Add(new JsonObject
            {
                ["name"] = u.Username,
                ["password"] = "",
                ["groups"] = new JsonArray(ChengeNames.Slug(roleId, "grp")),
            });
        }

        if (!userArr.Any(u => ((JsonArray)u!["groups"]!).Any(g =>
                groups.Any(gr => gr!["id"]!.GetValue<string>() == g!.GetValue<string>() &&
                                 gr["admin"]!.GetValue<bool>())))
           )
        {
            issues.Error("users.no_admin", "users.json",
                "工程里必须有至少一个属于管理员组的用户（37 §6.9），否则加载期报错");
            return null;
        }

        return new JsonObject { ["groups"] = groups, ["users"] = userArr };
    }

    // ── manifest 辅助 ──

    private static (int Width, int Height) ResolveScreen(IReadOnlyList<HmiScreen> screens, ChengeExportOptions options)
    {
        var first = screens.FirstOrDefault();
        var w = options.ScreenWidth > 0 ? options.ScreenWidth : (int)Math.Round(first?.Width ?? 800);
        var h = options.ScreenHeight > 0 ? options.ScreenHeight : (int)Math.Round(first?.Height ?? 480);
        return (w, h);
    }

    private static string ResolveHwProfile(HmiProject project, ChengeExportOptions options, ChengeIssueBag issues)
    {
        if (!string.IsNullOrWhiteSpace(options.HwProfile)) return options.HwProfile!;
        var target = project.HardwareTargetId;
        if (string.Equals(target, "f1c100s-kinseal", StringComparison.OrdinalIgnoreCase)) return "f1c100s-kinseal";
        if (string.IsNullOrWhiteSpace(target))
        {
            issues.Warn("manifest.hw_profile", "manifest.json",
                "工程没有指定硬件目标，按 f1c100s-kinseal 导出（与运行时不符会被拒绝加载）");
            return "f1c100s-kinseal";
        }
        issues.Warn("manifest.hw_profile", "manifest.json",
            $"硬件目标 {target} 不是 chenge-runtime 板卡，按 f1c100s-kinseal 导出");
        return "f1c100s-kinseal";
    }

    private JsonArray FeatureArray(HashSet<string> features)
    {
        var arr = new JsonArray();
        foreach (var f in features.OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!_schema.IsKnownFeature(f))
            {
                continue;
            }
            arr.Add(f);
        }
        return arr;
    }

    // ── 落盘 ──

    private List<string> WriteAll(IChengeExportSink sink, JsonObject manifest, Dictionary<string, JsonNode> files,
        ChengeAssets assets, ChengeIssueBag issues)
    {
        var written = new List<string>();
        sink.Begin();

        void Write(string relative, string content)
        {
            sink.Write(relative, Encoding.UTF8.GetBytes(content));
            written.Add(relative);
        }

        Write("manifest.json", manifest.ToJsonString(WriteOptions));
        foreach (var (relative, node) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (relative == "manifest.json") continue;
            Write(relative, node.ToJsonString(WriteOptions));
        }
        foreach (var (relative, bytes) in assets.Binaries.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sink.Write(relative, bytes);
            written.Add(relative);
        }

        var bytesTotal = assets.Binaries.Sum(kv => (long)kv.Value.Length);
        if (bytesTotal > 8 * 1024 * 1024)
            issues.Warn("asset.total_size", "assets/", $"图片总量 {bytesTotal / 1024 / 1024}MB 超过 8MB 建议上限（37 §6.15）");

        return written;
    }
}

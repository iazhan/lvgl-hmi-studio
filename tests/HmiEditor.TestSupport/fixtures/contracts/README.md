# 契约语料（冻结副本）

这几个 C 文件不是本仓库的构建输入，是**跨语言同源判据的语料**：编辑器侧的 MQTT 契约实现
（`src/HmiEditor.Core/Services/Mqtt*.cs`）与它们逐条比对样例表，改一边不改另一边就会红。

| 文件 | 被谁读 |
|---|---|
| `hmi_comm_test.c` | `MqttTopicAclTests`、`MqttCommandContractTests`、`MqttWriteGuardTests`、`MqttValueContractTests`、`MqttTlsContractTests`、`MqttAlarmStatusContractTests` |
| `mqtt_acl_contract.c` | `MqttTopicAclTests`（权限位只能来自生成头，不许手写） |
| `generated_security.h` | `MqttTopicAclTests`（权限位数值） |
| `mqtt_cmd_contract.h` | `MqttCommandContractTests`（ID/TTL/偏斜常量） |

**为什么是副本而不是原路径**：设备侧运行时（原 `firmware/t113/`）已退役、不在本仓库维护；
语料冻结在这里，判据仍然可判，但不再让人以为"仓库里还有一套设备侧实现"。

**改这些文件的时机**：只有当编辑器侧的契约真的变了（且已确认对上设备侧），才在这里同步改动 ——
它是判据的输入，不是随手可调的配置。另一个测试夹具 `../fangsong.ttf` 是 PDF 报表判据用的中文字体，
同样是"判据输入"，不是产品资产。

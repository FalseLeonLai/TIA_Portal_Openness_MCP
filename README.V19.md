# TIA Portal V19 引擎（2.8.0）

> 状态：**V19 构建 0 errors / 2 既有 warnings；离线测试 698 passed / 0 failed（687 基线 + 11 条账本脱敏/共享用例）；B1–B5 未真机**（2026-11-19）。
> **发布阻塞（Release blocker）**：源码已是 **2.8.0**，但 `runtime/v21/TiaMcpServer.exe` 仍是 **2.7.8.4**；
> 分发前必须在 Windows 上重新构建，否则 `scripts/Validate-Bundle.ps1 -Strict` 无法通过。本机是 Linux，
> 无法重建该 .NET Framework 4.8 二进制，这是本版唯一未清零的发布阻塞项。
> 核心链、块读取与导出、HTTP 编码、V19 边界、编译、生成、日常编辑（除改名）、HTTP 安全已真机验证；HMI 未测（测试工程无 HMI）。
> 已知问题：`ExportDeviceAml` 在 V19 上可能让 TIA 实例崩溃；`SetPlcTag` 改名在 V19 不受支持（`PlcTag.Name` 只读）。
>
> **给 AI agent**：先读 [`docs/AGENT_COOKBOOK.md`](docs/AGENT_COOKBOOK.md)（决策表 / 黄金路径 / 参数规则 / token 纪律），
> 经验库在 [`docs/AGENT_LEARNINGS.md`](docs/AGENT_LEARNINGS.md)。
> 全部 **239** 个工具（V19 编译面）的说明与索引：[`docs/TOOLS_REFERENCE.md`](docs/TOOLS_REFERENCE.md)（机器索引用 `docs/tools-index.csv`）。

## 这是什么

本分支从上游 V19 移植继续演进，**独立维护，不再向上游提交 PR**；上游代码只作参考基线。核心仍是把 **master 2.7.8 全量合并**进 `v19`，只保留 V19 无法编译的 API 的 guard：

- 编译目标 **Openness 19.0**（`Siemens.Collaboration.Net.TiaPortal.Packages.Openness 19.0.1725520202`）
- 新增 `TiaMcpServer.V19.csproj`，定义 `TIA_V19`，输出 `bin-v19/`
- 默认 TIA 版本 **19**（`Engineering.DefaultTiaMajorVersion`），Openness 初始化门槛 `>= 19`
- V19 缺失的 API 用 `#if !TIA_V19` 排除，运行期由 stub 返回 `NotSupportedOnVersion`
- 全量合并后 **233 个工具**：master 238 个减去 5 个 VCI 工具；4 个文档工具仍在但不可用。2.7.8.2 新增 2 个读取工具后为 **235**。

## 全量合并结果

| 项目 | 结果 |
|---|---|
| 合并上游 | master `2627ee0`（2.7.8），103 commits / 226 files |
| 合并冲突 | 2 个：`Program.cs`、`Siemens/Portal.cs` |
| V19 适配 | 约 30 行 guard + V19 csproj/stub/compat |
| 构建 | 0 errors，2 warnings（MSB3539、OpcUaLiveReader CS8602，均为既有） |
| 离线测试 | `TiaMcpServer.Tests` 472 passed / 0 failed |
| 工具数 | 238 → 233（5 个 VCI 不编译）；2.7.8.2 +2 → 235 |

关键 V19 适配：

- `Program.cs`：默认版本回退 `DefaultTiaMajorVersion`；Openness 初始化门槛 `>= 19`。
- `Siemens/EngineRouter.cs`：`TIA_V19` → `CompiledTiaMajorVersion=19`；sibling 查找认 `bin-v19`。
- `Siemens/Portal.Blocks.cs` / `Portal.Helpers.cs`：SIMATIC SD 文档 API 用 `#if !TIA_V19` 排除。
- `Siemens/Portal.Devices.cs`：V19 复用 V20 的 `CommunicationConnections` 反射降级。
- `Siemens/Portal.Software.cs` / `Portal.HwHmiConnection.cs`：跳过 V21 硬件层 HMI 连接。
- `McpServer.VersionControl.cs`：整个 VCI 文件 `#if !TIA_V19`（V19 没有 `MappedObject`）。

## V19 能力边界

| 能力 | V19 | 说明 |
|---|---|---|
| SIMATIC SD / S7DCL 文档导入导出 | ❌ | 4 个工具仍列出；V19 上返回 `InvalidParams` + V20 提示（见「已知限制」） |
| VCI（版本控制接口） | ❌ | 5 个工具不编译；`MappedObject` 为 V21+ |
| 硬件级 HMI 连接创建 | ⚠️ 降级 | V21 的 `CommunicationConnections` 不存在，走软件层；经典面板可能建不出 |
| 经典 HMI / WinCC Unified | ⬜ | 编译通过；测试工程无 HMI，画面 / 变量 / 连接 / 编译未真机验证 |
| PLC 块 / 变量表 / UDT / DB / 编译 | ✅ | 2.7.8.1 真机验证通过（含 NBSP 组名） |
| Cax/AML 设备导出 | ⚠️ | V19 真机导出会让 TIA 实例崩溃，见「已知限制」 |
| 软件单元读取 | ✅ | 真机调用通过（测试工程无软件单元） |
| 日常编辑 11 工具 | ⚠️ | 除 `SetPlcTag` 改名（V19 只读）外已真机验证 |
| 运行时只读监控（S7 / OPC UA） | ✅ | 不依赖 Openness，独立通道 |

## 2.7.8.2 新增（2026-10-31）

| 工具 | 用途 |
|---|---|
| `GetBlockInterface` | 读 FB/FC/OB/DB 的接口声明：Input / Output / InOut / Static / Temp / Constant；每个成员带类型、初值、注释、数组边界、嵌套结构。DB 的结构就是它的 Static 段。 |
| `GetTypeStructure` | 读 UDT / `PlcType` 的结构（成员、类型、初值、注释、数组、嵌套）。 |

- **为什么 V19 必须走 XML（官方 API 核对）**：V19 的官方 `Siemens.Engineering.xml` 里只有 `DataBlock.Interface`（返回 `PlcBlockInterface`，且只有扁平的 `Members`）；`PlcBlock` 只有 `InterfaceModifiedDate`，`FunctionBlock` / `Function` / `OrganizationBlock` 继承 `PlcBlock`，`PlcType` 也没有 `Interface` 属性。FB/FC/OB/UDT 没有 typed interface API，而 `PlcBlock.Export(FileInfo, ExportOptions)` / `PlcType.Export(...)` 本身就是西门子官方的 SimaticML 导出；`<Interface><Sections>` 是官方文档定义的结构。因此 XML 不是反射或私有 API，而是 V19 上唯一的官方通用路径。详见 `docs/V19-INTERFACE-API.md`。
- **前置条件**：块/类型必须一致（先编译）、PLC 必须离线（先 `GoOffline`）。在线调用会明确拒绝并给出恢复提示。
- **反射桥修复**：`GetObjectProperty` / `ListObjectChildren` 以前对「属性不存在」「路径中段为 null」「不是集合」都回 `OK` + 空数据；现在返回结构化 `InvalidParams`，并提示改用 `GetBlockInterface`。这是报告里 `Interface` 静默成功那一类的根因。
- **离线回归**：527 passed / 0 failed（本 PR 接口/列表用例 48 条；总数含既有基线）。
- **真机状态**：本轮新增的 `GetBlockInterface` / `GetTypeStructure` 尚未在 TIA V19 真机上验证；上面的 48 PASS 是 2.7.8.1 旧矩阵，不覆盖这两个工具。

## 2.7.8.3 / 2.7.8.4 修复（2026-11-18）

- **2.7.8.3 `DescribeBlockLogic` 完整接线**：每个 LAD 网络在能流摘要后输出逐引脚接线表——
  `IN/EN/IN1/IN2/PT` 等输入脚写来源条件（串联 `·`、并联 `+`），`Q/ET/ENO/OUT` 等输出脚写去向，
  未接标 `未接`；一个脚的多条 `<Wire>` 全部保留。TON 的 `IN` 大小写、Add 的 EN/IN1/IN2 分支不再丢。
- **2.7.8.4 B1–B5**：
  - B1：`<Call>` 调用框独立输出行（`[82] Call #epos (FB284)`），引脚目标带实例/块名，Call 引脚方向从线另一端推断。
  - B2：箱式 TON 数组实例保留下标：`TON[Delay[1]]`。
  - B3：空网络 / 未识别部件 / 不支持语言分开提示。
  - B4：`GetState` 返回 `attachableProjects` 和 `nextAction`，`Bootstrap` 同步；不自动绑定，避免误绑用户工程。
  - B5：`GetBlockInfo` 的 `Block not found` 复用 RECOVERY，并按普通空格 ↔ U+00A0 归一化找候选。
- **验证**：V19 构建 0 errors / 2 既有 warnings；离线 **546 passed / 0 failed**（新增 7 条）。
  B1–B5 尚未真机，需用含 FB/FC 调用、多个同名 TON、空网络和 NBSP 路径的块复测。

## v2.8.0 Trust & Batch（2026-11-19）

工具数 235 → 239（V19）。这一版不加新的 TIA 业务能力，只让模型在**动手之前**能判断
「能不能做、要什么前置、做完怎么审计」：

| 工具 | 层 | 用途 |
|---|---|---|
| `GetToolCapabilities` | L0 Meta | 每个工具的 access（读/写/删除）、status（Verified / Implemented / Unverified / Planned）、requires（portal / online / confirm / dryRun / V20+ / V21+）和 Evidence。`FindTools` 的结果行也会带 `[read|verified]` 摘要。 |
| `PlanBatch` | L1 Orchestration | 对一批 `{tool, argumentsJson}` 做静态预检：工具存在、版本支持、DirectOnly 策略、写操作确认、参数能否绑定；**不执行、不碰 Portal**。 |
| `RunBatch` | L1 Orchestration（Destructive / DirectOnly） | 复跑同一份预检，通过后在进程级 Portal 锁内顺序执行；逐操作写 JSONL 账本，失败可按 `continueOnError` 停止或继续。 |
| `GetOpsLedger` | L0 Meta | 读取账本尾部（默认 50 行）。账本只存参数 SHA-256，不落明文。路径由 `TIA_MCP_LEDGER` 覆盖，默认 `%LOCALAPPDATA%\TiaMcpServer\ops-ledger.jsonl`。 |

示例：

```json
[
  {"tool":"GetBlocks","argumentsJson":"{\"softwarePath\":\"PLC_1\"}"},
  {"tool":"SetPlcTag","argumentsJson":"{\"softwarePath\":\"PLC_1\",\"tagName\":\"Tag_1\",\"tagTableName\":\"Default tag table\",\"dataType\":\"Int\",\"address\":\"%MW100\"}"}
]
```

先调 `PlanBatch(operationsJson, confirm=true)` 看 `canRun` 和逐条 `issues`，再调
`RunBatch(operationsJson, confirm=true)`。设计说明见 [`docs/TRUST-LAYER.md`](docs/TRUST-LAYER.md)。

## 默认工具档位

master 2.4+ 默认 **lite**（核心工具）。要看到全部 239 个工具：

```bat
rem MCP 客户端启动参数
--profile full

rem 或环境变量
set TIA_MCP_PROFILE=full
```

lite 档下仍可通过 `FindTools` + `CallTool` 按需调用非 lite 工具。`ScaffoldProject` 和删除类工具默认 `dryRun=true`：先 dry run，确认后再传 `dryRun=false`。

## 构建

### Windows（推荐）

要求：TIA Portal **V19** 已安装、.NET Framework 4.8、.NET SDK（`dotnet` 在 PATH）。

```bat
rem TIA 装在默认位置
build-v19.cmd

rem 非标准安装路径
build-v19.cmd "D:\app\TIA19\Portal V19"
```

产物：`tools\tiaportal-mcp\src\TiaMcpServer\bin-v19\Release\net48\TiaMcpServer.exe`

### Linux / CI（交叉编译）

NuGet 包本身不含 Openness DLL；把 Windows 机器上的 `Portal V19\PublicAPI\V19` 整个目录拷过来：

```bash
./build-v19.sh "/path/to/PublicAPI/V19"
```

> Siemens 的 DLL 有版权，**不要提交进仓库**。

## 运行

```bat
rem 打印版本，不需要 TIA
tia-v19.cmd version
rem 命令用法
tia-v19.cmd help
rem spec 字段说明，离线
tia-v19.cmd schema
rem 离线冒烟：version / self-test / gen --dry-run
smoke-v19.cmd
rem 追加 connect self-test，会启动 TIA
smoke-v19.cmd --with-tia
```

MCP 客户端配置（Cursor / VS Code / Claude Desktop），全量工具：

```json
{
  "mcpServers": {
    "tia-portal-v19": {
      "command": "C:\\path\\to\\TiaMcpServer.exe",
      "args": ["--tia-portal-location", "C:\\Program Files\\Siemens\\Automation\\Portal V19", "--tia-major-version", "19", "--profile", "full"],
      "env": { "TIA_MCP_PROFILE": "full" }
    }
  }
}
```

TIA 装在默认位置且只装 V19 时，`--tia-major-version` 可省略（会自动检测）。

## 真机验证（2026-10-30，2.7.8.1）

工程 `<TEST_PROJECT>`（S7-1500，无 HMI），TIA V19。58 条验收矩阵：
**48 PASS / 2 FAIL / 1 BLOCKED / 7 N/A**；必过组全绿（编码回归 5/5、V19 边界 4/4、删除 dryRun 2/2）。

| # | 检查项 | 结果 |
|---|---|---|
| 1 | `tia-v19 version` → 2.7.8.1；自动检测到 19 | ✅ |
| 2 | `--run-capability-self-test --self-test-connect` → 初始化 + `IsConnected=True` | ✅ |
| 3 | `gen` 最小 spec → 建工程 / 加 PLC / DB / 变量表 | ✅ 5 ok / 0 failed |
| 4 | 编译（软件 / 硬件） | ✅ 0 errors |
| 5 | 保存 | ✅ |
| 6 | `ExportAsDocuments` 等 4 个文档工具 | ✅ 明确拒绝（`InvalidParams` + V20 提示；见已知限制） |
| 7 | `DescribeBlockLogic` 读 SCL / LAD 块 | ✅（含 U+00A0 组名） |
| 8 | `ExportBlock` / `ExportBlocks` 用 `Program blocks/...` 根前缀 | ✅ |
| 9 | 版本路由日志不出现 “rerouting to V21” | ✅ |
| 10 | HTTP 请求体 UTF-8（NBSP / 中文） | ✅ 码点级验证 |
| 11 | 删除类工具 `dryRun=true` 语义 | ✅ |
| 12 | `ExportDeviceAml`（CAx/AML） | ❌ 见已知问题 1 |
| 13 | `SetPlcTag` 改名 | ❌ 见已知问题 2 |
| 14 | HMI（经典 / Unified） | ⬜ N/A：测试工程无 HMI |
| 15 | SCL 源码导入 | ⬜ 未覆盖 |

## 已知限制

- **`ExportDeviceAml` 可能让 TIA 实例崩溃（V19 真机实测）。** `CaxProvider.Export` 抛异常后
  TIA 进程退出，`Portal` 留下 disposed 句柄；随后 `GetState` 会报 `Access to a disposed object`。
  `Connect` + `OpenProject` 可恢复。当前没有故障隔离；在生产工程上调用前先归档，或暂时不用该工具。
- **`SetPlcTag` 改名在 V19 不受支持。** `PlcTag.Name` 是只读属性，`SetAttribute("Name")` 会报
  `'set_Name' is not supported`；请在 TIA 界面改名（代码引用会跟随），或新建变量后删除旧变量
  （代码/HMI 的按名引用不会自动迁移）。
- **文档 API 的错误码与文档不一致。** V19 上 4 个文档工具先命中 `TiaMajorVersion < 20` 前置门，
  返回 `InvalidParams` + `requires TIA Portal V20 or newer`，不是 `NotSupportedOnVersion`；
  拒绝行为正确，错误码待统一。
- **HMI 未验证。** 测试工程没有 HMI；经典 / Unified 的画面、变量、连接、编译仍待有 HMI 的工程。
- VCI 5 个工具在 V19 上不存在；4 个文档工具仍列出但不可用。
- V19 的 Openness 是单体 `Siemens.Engineering.dll`（V21 起拆分）；升级上游时保持 `TIA_V19` guard。
- 默认 lite 档；需要全量工具时显式 `--profile full` / `TIA_MCP_PROFILE=full`。

## 维护策略

- `v19` 独立维护，**不再向上游提交 PR**；上游代码仅作参考基线。
- 每次分支维护/发版固定流程：V19 构建 → 离线测试 → 更新 `CHANGELOG.md` / `README.V19.md` / 工具文档 → 打 `v<version>-v19` tag → 发布 GitHub Release（完整包 + 热修包 + SHA256）。逐条命令见 [`docs/RELEASE_CHECKLIST_V19.md`](docs/RELEASE_CHECKLIST_V19.md)。
- 每次合并上游或大改后重跑真机矩阵；未真机的修复必须在 Release notes 里标明。
- 连续 6 个月无 V19 issue 且上游不再兼容 V19 API → 仅安全修复；再 6 个月 → 归档。

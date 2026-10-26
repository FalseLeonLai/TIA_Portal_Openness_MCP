# TIA Portal V19 引擎（master 2.7.8 全量合并）

> 状态：**V19 构建 0 errors / 2 既有 warnings；上游离线测试 472 passed**（2026-10-26）。
> 核心链路真机结果（连接/建项目/加 PLC/DB/变量表/编译/保存）来自 2.2.1 基线，**全量合并后需要重跑**。
> 未验证：HMI（经典 / Unified）、SCL 源导入、VCI（V19 不可用）、`DescribeBlockLogic` 真机调用。
>
> **给 AI agent**：先读 [`docs/AGENT_COOKBOOK.md`](docs/AGENT_COOKBOOK.md)（决策表 / 黄金路径 / 参数规则 / token 纪律），
> 经验库在 [`docs/AGENT_LEARNINGS.md`](docs/AGENT_LEARNINGS.md)。
> 全部 **233** 个工具（V19 编译面）的说明与索引：[`docs/TOOLS_REFERENCE.md`](docs/TOOLS_REFERENCE.md)（机器索引用 `docs/tools-index.csv`）。

## 这是什么

上游 `v19` 已合入本项目的 V19 移植。本分支不再逐条回移，而是把 **master 2.7.8 全量合并**进 `v19`，只保留 V19 无法编译的 API 的 guard：

- 编译目标 **Openness 19.0**（`Siemens.Collaboration.Net.TiaPortal.Packages.Openness 19.0.1725520202`）
- 新增 `TiaMcpServer.V19.csproj`，定义 `TIA_V19`，输出 `bin-v19/`
- 默认 TIA 版本 **19**（`Engineering.DefaultTiaMajorVersion`），Openness 初始化门槛 `>= 19`
- V19 缺失的 API 用 `#if !TIA_V19` 排除，运行期由 stub 返回 `NotSupportedOnVersion`
- 全量合并后 **233 个工具**：master 238 个减去 5 个 VCI 工具；4 个文档工具仍在但不可用

## 全量合并结果

| 项目 | 结果 |
|---|---|
| 合并上游 | master `2627ee0`（2.7.8），103 commits / 226 files |
| 合并冲突 | 2 个：`Program.cs`、`Siemens/Portal.cs` |
| V19 适配 | 约 30 行 guard + V19 csproj/stub/compat |
| 构建 | 0 errors，2 warnings（MSB3539、OpcUaLiveReader CS8602，均为既有） |
| 离线测试 | `TiaMcpServer.Tests` 472 passed / 0 failed |
| 工具数 | 238 → 233（5 个 VCI 不编译） |

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
| SIMATIC SD / S7DCL 文档导入导出 | ❌ | 4 个工具仍列出，调用返回 `NotSupportedOnVersion` |
| VCI（版本控制接口） | ❌ | 5 个工具不编译；`MappedObject` 为 V21+ |
| 硬件级 HMI 连接创建 | ⚠️ 降级 | V21 的 `CommunicationConnections` 不存在，走软件层；经典面板可能建不出 |
| 经典 HMI / WinCC Unified | ✅（编译） | 新 HMI 读属性 / HMI 编译 / 离散报警工具未真机验证 |
| PLC 块 / 变量表 / UDT / DB / 编译 | ✅ | 核心链路 2.2.1 已实测；合并后待重跑 |
| Cax/AML 设备导出 | ✅（编译） | 未真机验证 |
| 软件单元读取 | ✅（编译） | 未真机验证 |
| 日常编辑 11 工具 | ✅（编译） | 未真机验证 |
| 运行时只读监控（S7 / OPC UA） | ✅ | 不依赖 Openness，独立通道 |

## 默认工具档位

master 2.4+ 默认 **lite**（核心工具）。要看到全部 233 个工具：

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

## 真机验证清单

前 7 项是 **2.2.1 基线**的实测结果；2.7.8 全量合并后必须重跑，不能直接沿用。

| # | 检查项 | 结果 |
|---|---|---|
| 1 | `tia-v19 version` → 能启动，版本 2.7.8；自动检测到 19 | ⬜ 合并后待测（2.2.1 基线 ✅） |
| 2 | `--run-capability-self-test --self-test-connect` → Openness 初始化 + `IsConnected=True` | ⬜ 合并后待测（2.2.1 基线 ✅） |
| 3 | `gen` 最小 spec → `createProject` 成功 | ⬜ 合并后待测（2.2.1 基线 ✅） |
| 4 | 添加 PLC（S7-1200 1211C） | ⬜ 合并后待测（2.2.1 基线 ✅） |
| 5 | 建 GlobalDB + 变量表 | ⬜ 合并后待测（2.2.1 基线 ✅） |
| 6 | 编译 | ⬜ 合并后待测（2.2.1 基线 ✅） |
| 7 | 保存 | ⬜ 合并后待测（2.2.1 基线 ✅） |
| 8 | SCL 源码导入 | ⬜ 待测 |
| 9 | HMI 冒烟：经典面板 / Unified 画面 | ⬜ 待测 |
| 10 | `ExportAsDocuments` → 应返回 `NotSupportedOnVersion` | ⬜ 待测 |
| 11 | `DescribeBlockLogic` 读一个已编译块（LAD/SCL） | ⬜ 待测 |
| 12 | `ExportBlock` 用 `Program blocks/...` 根前缀 | ⬜ 待测 |
| 13 | 版本路由日志不出现 “rerouting to V21” | ⬜ 待测 |

## 已知限制

- **全量合并只做了编译 + 上游离线测试**；真实 TIA V19 的 HMI、Cax/AML、软件单元、日常编辑、在线监控尚未验证。
- VCI 5 个工具在 V19 上不存在；4 个文档工具返回 `NotSupportedOnVersion`。
- V19 的 Openness 是单体 `Siemens.Engineering.dll`（V21 起拆分）；升级上游时保持 `TIA_V19` guard。
- 默认 lite 档；需要全量工具时显式 `--profile full` / `TIA_MCP_PROFILE=full`。

## 维护策略

- `v19` 跟随 master 合并，不再逐条回移；每次合并后重跑本文件的真机矩阵。
- 连续 6 个月无 V19 issue 且 master 不再兼容 V19 API → 仅安全修复；再 6 个月 → 归档。

# TIA Portal V19 引擎（社区移植）

> 状态：**编译通过 + 核心链路真机验证通过**（2026-10-25，TIA Portal V19 + S7-1200 1211C）。
> 已验证：连接 → 建项目 → 加 PLC → 建 GlobalDB → 建变量表 → 编译 0 错误 → 保存。
> 未验证：HMI（经典 / Unified）、SCL 源导入、LAD 文档导入（V19 无此 API）。
>
> **给 AI agent**：先读 [`docs/AGENT_COOKBOOK.md`](docs/AGENT_COOKBOOK.md)（决策表 / 黄金路径 / 参数规则 / token 纪律），
> 经验库在 [`docs/AGENT_LEARNINGS.md`](docs/AGENT_LEARNINGS.md)；每完成一次任务按第 8 节追加一行。
> 全部 189 个工具的说明与索引：[`docs/TOOLS_REFERENCE.md`](docs/TOOLS_REFERENCE.md)（机器索引用 `docs/tools-index.csv`）。

## 这是什么

上游仓库的 `v19` 分支只是给社区预留的占位分支（内容 = 2026-06-11 的 2.2.1 主线快照，仍引用 V20/V21 的 Openness 包，没有 V19 的 exe）。上游作者在 issue #52 中确认「目前还没有适配 V19」。

本分支（`v19-port`）在 2.2.1 代码基线上做了真正的 V19 适配：

- 编译目标换成 **Openness 19.0**（`Siemens.Collaboration.Net.TiaPortal.Packages.Openness 19.0.1725520202`）
- 新增 `TiaMcpServer.V19.csproj`，定义 `TIA_V19`，输出到 `bin-v19/`
- 默认 TIA 版本 21 → **19**（`Engineering.DefaultTiaMajorVersion`）
- Openness 初始化门槛从 `>= 20` 放宽到 `>= 19`
- V19 没有的 API 在编译期剔除，运行期返回明确的 `NotSupportedOnVersion`

## 改动的文件

| 文件 | 改动 |
|---|---|
| `TiaMcpServer.V19.csproj` | 新增：V19 工程，net48 + Openness 19.0 + `TIA_V19` |
| `Siemens/V19Compat.cs` | 新增：`ImportDocumentOptions` 占位枚举（V20+ 类型） |
| `Siemens/Portal.Documents.V19.cs` | 新增：4 个文档工具的 V19 stub，抛 `NotSupportedOnVersion` |
| `Siemens/Portal.cs` | 文档导入/导出 4 个方法用 `#if !TIA_V19` 排除；`CommunicationConnections` 在 V19 上复用 V20 的反射降级路径 |
| `Siemens/Engineering.cs` | 新增 `DefaultTiaMajorVersion` 常量（V19 build = 19） |
| `Siemens/Openness.cs` | 默认版本、兜底版本改用该常量 |
| `Program.cs` | 版本兜底改用常量；Openness 初始化门槛 `>= 19` |

## V19 能力边界

V19 的 Openness 程序集里**不存在**下面这些 API，代码层面绕不过去：

| 能力 | V19 | 说明 |
|---|---|---|
| SIMATIC SD / S7DCL 文档导入导出 | ❌ | `ExportAsDocuments` / `ImportFromDocuments` / `DocumentExportResult` 均为 V20+ 新增 |
| 硬件级 HMI 连接创建 | ❌ | `Siemens.Engineering.HW.CommunicationConnections` 为 V21+ |
| 经典 HMI / WinCC Unified | ✅（编译层面） | `Siemens.Engineering.Hmi` / `HmiUnified` 在 V19 中存在 |
| PLC 块 / 变量表 / UDT / DB / 编译 | ✅（编译层面） | 相关 API 在 V19 中存在 |
| 运行时只读监控（S7 / OPC UA） | ✅ | 不依赖 Openness，独立通道 |

被剔除的 4 个文档工具：`ExportAsDocuments`、`ExportBlocksAsDocuments`、`ImportFromDocuments`、`ImportBlocksFromDocuments`。它们在 V19 上仍会出现在工具列表里，但调用时返回 `NotSupportedOnVersion`，错误信息会说明需要 V20+。

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

NuGet 包本身**不含** Openness DLL，编译时从 `SiemensEngineeringDirectory` 目录取 `*.dll`。把 Windows 机器上的 `Portal V19\PublicAPI\V19` 整个目录拷过来即可：

```bash
./build-v19.sh "/path/to/PublicAPI/V19"
```

> Siemens 的 DLL 有版权，**不要提交进仓库**。

## 运行

```bat
tia-v19.cmd version      :: 打印版本（不需要 TIA 在运行）
tia-v19.cmd help         :: 命令用法
tia-v19.cmd schema       :: spec 字段说明（离线）
```

MCP 客户端配置（Cursor / VS Code / Claude Desktop）：

```json
{
  "mcpServers": {
    "tia-portal-v19": {
      "command": "C:\\path\\to\\TiaMcpServer.exe",
      "args": ["--tia-portal-location", "C:\\Program Files\\Siemens\\Automation\\Portal V19", "--tia-major-version", "19"]
    }
  }
}
```

TIA 装在默认位置且只装了 V19 时，`--tia-major-version` 可省略（会自动检测）。

## 真机验证清单

按顺序做，前一步不过不要往下走：

| # | 检查项 | 结果 |
|---|---|---|
| 1 | `tia-v19 version` → 能启动，版本 2.2.1.0；自动检测到 19 | ✅ 2026-10-25 |
| 2 | `--run-capability-self-test --self-test-connect` → Openness 初始化 + `IsConnected=True` | ✅ |
| 3 | `gen` 最小 spec → `createProject` 成功 | ✅ `V19_Smoke_01.ap19` |
| 4 | 添加 PLC（S7-1200 1211C，6ES7211-1BE40-0XB0） | ✅ |
| 5 | 建 GlobalDB + 变量表 | ✅ |
| 6 | 编译 | ✅ `state=Success errors=0 warnings=0` |
| 7 | 保存 | ✅ |
| 8 | SCL 源码导入 | ⬜ 待测 |
| 9 | HMI 冒烟：经典面板 / Unified 画面 | ⬜ 待测 |
| 10 | `ExportAsDocuments` → 应返回 `NotSupportedOnVersion` | ⬜ 待测 |

前 7 项在 TIA Portal V19（Windows 虚拟机）+ S7-1200 1211C 上实测通过，整条 `gen` 用了约 1 分钟
（TIA 已预热的情况下）。

## 已知限制

- **只验证了核心链路**：连接/建项目/加 PLC/建 DB 与变量表/编译/保存已实测；HMI、SCL 导入、在线监控等尚未验证
- 基线是 `v19` 分支的 2.2.1（189 工具），**不含** master 2.7.7 的 99 个提交和后续新增工具
- 4 个文档工具不可用（V19 无此 API）
- V19 的 Openness 是单体 `Siemens.Engineering.dll`（V21 起才拆分为多个 DLL），升级上游代码时注意这个差异

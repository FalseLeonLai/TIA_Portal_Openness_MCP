# TIA Portal V19 引擎（社区移植）

> 状态：**编译通过（0 错误），尚未真机验证**。本文档说明改了什么、怎么构建、怎么验证，以及哪些能力在 V19 上确实不存在。

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

1. `tia-v19 version` → 能启动，版本号 2.2.1
2. 连接 TIA Portal V19（`ConnectPortal`）→ 能拉起/附着 Portal 进程
3. `CreateProject` → 新建项目成功
4. 添加 PLC（S7-1200/1500）+ 硬件配置
5. 建变量表、UDT、GlobalDB
6. 用 SCL 源码建块 → 导入 → 编译，0 错误
7. HMI 冒烟：经典面板建画面；Unified 建画面
8. 调 `ExportAsDocuments` → 应返回 `NotSupportedOnVersion`，而不是崩溃或假成功

## 已知限制

- **未真机验证**：目前只保证编译通过，运行时行为需要 Windows + TIA V19 实测
- 基线是 `v19` 分支的 2.2.1（189 工具），**不含** master 2.7.7 的 99 个提交和后续新增工具
- 4 个文档工具不可用（V19 无此 API）
- V19 的 Openness 是单体 `Siemens.Engineering.dll`（V21 起才拆分为多个 DLL），升级上游代码时注意这个差异

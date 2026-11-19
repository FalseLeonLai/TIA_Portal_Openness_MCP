# v2.8.0 Trust & Batch Layer — 实现规格

> 目标：把 PLC Code（云擎）里最值得学的三件事落到本仓库：
> 1) **能力矩阵**：每个工具带 access / status / requires / evidence，模型和宿主在调用前就知道"能不能写、验没验证、需要什么前置"。
> 2) **批次预检**：一次提交多个工具调用，先静态预检（工具存在、参数可绑定、权限/确认门、版本支持），再决定是否执行。
> 3) **操作账本 + Portal 串行锁**：批次执行全程串行，逐操作记录到 JSONL 账本，便于审计与恢复。

本规格是给实现/评审会话的契约。所有新代码必须遵守仓库既有风格：中文注释解释"为什么"，零依赖逻辑抽到可离线测试的文件，错误信息要能指导恢复。

---

## 0. 非目标

- 不新增任何 TIA Openness 业务能力（不改 Portal.*.cs 的业务逻辑）。
- 不改现有 235 个工具的业务行为、返回类型或默认参数（本版新增 4 个工具，合计 239 个；
  `FindTools` 仅追加 `[access|status]` 能力标签，返回类型不变）。
- 不引入新 NuGet 依赖。
- 不做 UI、不做 HTTP 路由改造。

---

## 1. 新增文件

### 1.1 `ModelContextProtocol/ToolCapability.cs`（零依赖）

```csharp
public enum ToolAccess { Read, Write, Delete }
public enum ToolStatus { Verified, Implemented, Unverified, Planned }

public sealed class ToolCapabilityInfo
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "General";
    public ToolAccess Access { get; set; }
    public ToolStatus Status { get; set; }
    public bool Destructive { get; set; }
    public bool DirectOnly { get; set; }
    public bool OpenWorld { get; set; }
    public string[] Requires { get; set; } = Array.Empty<string>();
    public string Evidence { get; set; } = "";
}
```

`public static class ToolCapabilities`：

- `Classify(string name, string description, int tiaMajorVersion = 0)` → `ToolCapabilityInfo`
- `All(IEnumerable<(string Name, string Description)> tools, int tiaMajorVersion = 0)` → `IReadOnlyList<ToolCapabilityInfo>`

规则：

1. **Category**：从描述前缀 `[L0][Meta]` / `[L2][PLC-Software]` 取第二个方括号；`[L2][Category:PLC-Online]` 去掉 `Category:` 前缀；没有标签时用 `General`。
2. **Access**：
   - `ToolSafety.Classify(name).ReadOnly` → `Read`
   - 否则名字以 `Delete` 开头 → `Delete`
   - 否则 → `Write`
3. **Destructive / DirectOnly / OpenWorld**：直接取 `ToolSafety.Classify(name)`。
4. **Status**（保守、可解释）：
   - `Planned`：版本门控且当前版本不支持。首批只处理：
     - 四个 SD 文档工具 `ExportAsDocuments`、`ExportBlocksAsDocuments`、`ImportFromDocuments`、`ImportBlocksFromDocuments` 在 `tiaMajorVersion > 0 && tiaMajorVersion < 20` 时 → `Planned`，Evidence `"SIMATIC SD requires V20+"`。
     - VCI 真实工具名（`GetVersionControlWorkspaces`、`CreateVersionControlWorkspace`、`GetVersionControlStatus`、`SyncVersionControlWorkspace`、`ConnectProjectToWorkspace`）在 `tiaMajorVersion > 0 && tiaMajorVersion < 21` 时 → `Planned`，Evidence `"Version control requires V21+"`。匹配规则用 name 包含 `"VersionControl"`，外加 `ConnectProjectToWorkspace`；`"Vci"` 前缀保留作防御。
   - `Verified`：仅限 `VerifiedTools` 中带**逐工具 Evidence** 的工具（见下）。不允许再用一句共用的笼统证据。
   - `Unverified`：
     - 名字包含 `"Hmi"`（一个子串覆盖全系，不再逐项列出）；
     - `UnverifiedTools` 显式集合：`GetBlockInterface`、`GetTypeStructure`（README.V19 2026-11-18 明确未真机验证）、`ExportAsDocuments`、`ImportBlocksFromDocuments`（无活工程实测）、五个 VCI 工具名（V21 门通过后仍无仓库内验证）；
     - SD 文档工具在 V20+ 但不在 `VerifiedTools` 者（即上面两个）；
     - SD 文档工具在版本未知（`0`）时 → `Unverified`（不能断言 `Planned`）。
   - `Verified` 优先于 `Unverified`，但 `Planned` 优先于两者。两个显式集合必须不相交（测试钉住）。
   - 其余 → `Implemented`。
5. **Requires**（字符串数组，按固定顺序去重）：
   - `ToolSafety.TouchesPortal` → `"portal"`
   - `ToolSafety.OpenWorld` → `"online"`
   - `Destructive` → `"confirm"`
   - **显式 `DryRunTools` 集合** → `"dryRun"`（不再按描述是否包含 `dryRun` 判断）。集合来自真实声明了 `bool dryRun` 参数的工具：
     `DeletePlcBlock`、`DeletePlcType`、`DeletePlcTagTable`、`DeletePlcTag`、`DeletePlcWatchTable`、`DeleteDevice`、`SetDeviceItemIoAddress`、`ScaffoldProject`、`PlugDeviceItem`、`PlcBuildAndImport`、`ImportPlcProgramFromDirectory`、`SyncVersionControlWorkspace`、`ConnectProjectToWorkspace`。
     注意：`DeletePlcExternalSource` 和 `ImportBlocksFromDirectory` **没有** `dryRun` 参数，不得加入；`GetToolCapabilities` 的描述里虽然出现 `dryRun` 一词，也不得因此要求 `dryRun`。
   - 版本门控 → `"V20+"` / `"V21+"`
   - 空数组表示无额外前置。
6. **VerifiedTools**（逐工具证据；只放能指回具体文档句子的真机记录）：
   - Evidence `"README.V19 真机矩阵 2026-10-30"`：
     `ScaffoldProject`、`CompileSoftware`、`CompileHardware`、`SaveProject`、`DescribeBlockLogic`、`ExportBlock`、`ExportBlocks`、`GetBlocks`、`GetBlockInfo`、`GetPlcTags`、`GetPlcTagTables`、`CreatePlcTagTable`、`DeletePlcBlock`、`GetSoftwareUnits`。
   - 运行时读值工具不在上面的 Openness 矩阵里；每个工具单独引用
     `docs/在线实时读值_使用指南.md`（§八，CPU 1211C @192.168.0.32）：
     `ReadPlcLiveValuesS7` → `"docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，S7 M/I/Q + 非优化 DB）"`；
     `ReadPlcLiveValuesOpcUa` → `"docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，OPC UA 节点）"`。
   - `SetPlcTag`：Evidence `"README.V19 真机矩阵 2026-10-30（改名除外，见已知问题 2）"`。
   - Evidence `"CHANGELOG 2.2.5 / 2.2.7 V21 真机"`：`DownloadToPlc`、`ConnectIsolated`。
   - Evidence `"CHANGELOG 2.2.9 V20/V21 活工程实测"`：`ImportFromDocuments`、`ExportBlocksAsDocuments`（仅当 V20+ 版本门通过时才 `Verified`；V19 → `Planned`，版本未知 → `Unverified`）。
   - **不在上述四组里的工具一律不得标 `Verified`**，即使历史上在真机上用过但没有可引用的 per-tool 记录（例如 `Connect`、`OpenProject`、`ImportBlock`）。
7. **Query helper**：过滤/分词/排序/夹取抽到零依赖的 `ToolCapabilityQuery`：
   ```csharp
   public static class ToolCapabilityQuery
   {
       public const int DefaultLimit = 50; // limit <= 0
       public const int MaxLimit = 500;     // limit > 500
       public static int ClampLimit(int limit);

       public static IReadOnlyList<ToolCapabilityInfo> Filter(
           IEnumerable<ToolCapabilityInfo> capabilities,
           string query, string status, string access, int limit,
           out int total, out string? error);
   }
   ```
   `ToolCapabilityInfo` 增加 `internal string Description`（只给查询用；System.Text.Json 默认不序列化 internal，不改变 JSON 契约）。

### 1.2 `ModelContextProtocol/BatchPlanner.cs`（零依赖）

```csharp
public sealed class BatchOperationInput
{
    public string Tool { get; set; } = "";
    public string ArgumentsJson { get; set; } = "";
}

public sealed class BatchOperationPlan
{
    public int Index { get; set; }
    public string Tool { get; set; } = "";
    public ToolAccess Access { get; set; }
    public ToolStatus Status { get; set; }
    public bool Destructive { get; set; }
    public bool DirectOnly { get; set; }
    public bool WillRun { get; set; }
    public bool Known { get; set; }
    public string[] Issues { get; set; } = Array.Empty<string>();
}

public sealed class BatchPlan
{
    public bool CanRun { get; set; }
    public int MaxOperations { get; set; }
    public string[] PlanIssues { get; set; } = Array.Empty<string>();
    public List<BatchOperationPlan> Operations { get; set; } = new();
}
```

```csharp
public static class BatchPlanner
{
    public const int DefaultMaxOperations = 200;

    public static BatchPlan Plan(
        IReadOnlyList<BatchOperationInput> operations,
        Func<string, ToolCapabilityInfo?> lookup,
        bool confirm,
        bool allowDirectOnly,
        int maxOperations = DefaultMaxOperations);
}
```

规则：

- `operations == null` / 空 → `PlanIssues += "batch is empty"`，`CanRun=false`。
- `maxOperations` 夹在 1..200；超过 → `PlanIssues += "batch has N operations; the limit is 200"`，`CanRun=false`。
- 每个 op：
  - `Tool` 空 → issue `"operation i: tool is required"`，`WillRun=false`。
  - `lookup(tool)` 为空 → issue `"operation i: unknown tool 'X'"`，`WillRun=false`。
  - `Status == Planned` → issue `"operation i: 'X' is not supported on this TIA version (planned)"`。
  - `DirectOnly && !allowDirectOnly` → issue `"operation i: 'X' must be called by its own name; set allowDirectOnly=true to batch it"`。
  - `Access != Read && !confirm` → issue `"operation i: 'X' writes; confirm=true is required"`。
  - 嵌套 `RunBatch`（名字大小写不敏感）→ issue `"operation i: RunBatch cannot be nested inside a batch"`
    （`BatchGate` 是不可重入的 `SemaphoreSlim`，嵌套会等自己到死锁）。
  - 嵌套 `CallTool` → issue `"operation i: CallTool cannot be nested; name the target tool directly"`
    （它只是同一个执行体的另一个入口）。
  - `InvokeObject` / `InvokeService` 且 `argumentsJson` 里 `allowWrite=true`（JSON true 或字符串
    `"true"`）→ issue `"operation i: 'X' with allowWrite=true cannot be nested; call it by its own
    name so the host names it"`。
  - `ArgumentsJson` 非空且不是 JSON object → issue `"operation i: argumentsJson must be a JSON object"`。
  - `WillRun = Issues.Length == 0`。
  - `Known = lookup(tool) != null`。为 `false` 时 `Access` / `Status` 仍是枚举零值
    （`Read` / `Verified`），调用方不得把它们当成能力声明。
- `CanRun = PlanIssues.Length == 0 && Operations.All(o => o.WillRun)`。

> 参数绑定预检由服务端适配层负责（用 `ReflectiveToolInvoker.Bind`），`BatchPlanner` 只做零依赖的静态规则，便于离线测试。适配层把绑定错误追加到对应 `Issues`，并重算 `WillRun`/`CanRun`。

### 1.3 `ModelContextProtocol/OpsLedger.cs`（零依赖）

```csharp
public sealed class OpsLedgerEntry
{
    public string Timestamp { get; set; } = "";
    public string BatchId { get; set; } = "";
    public int Index { get; set; }
    public string Tool { get; set; } = "";
    public string Access { get; set; } = "";
    public string Status { get; set; } = ""; // ok / error / skipped
    public long DurationMs { get; set; }
    public string ArgumentsSha256 { get; set; } = ""; // 对原始 ArgumentsJson 字符串求 SHA-256，不落明文参数
    public string? Error { get; set; }
}

public static class OpsLedger
{
    public static string DefaultPath { get; } // TIA_MCP_LEDGER 环境变量优先；否则 %LOCALAPPDATA%\TiaMcpServer\ops-ledger.jsonl
    public static void Append(string path, OpsLedgerEntry entry);
    public static IReadOnlyList<string> ReadTail(string path, int limit);
}
```

- 目录不存在时创建。
- 追加一行 UTF-8 JSON（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`）。
- 读取时文件不存在返回空列表；最多返回最后 `limit` 行（默认 50，夹 1..500）。
- 不记录明文参数；只记录 SHA-256（对 `ArgumentsJson` 原始字符串做 SHA-256，小写 hex）。

### 1.4 `ModelContextProtocol/McpServer.Capabilities.cs`

新增工具：

```csharp
[McpServerTool(Name = "GetToolCapabilities")]
[Description("[L0][Meta] ...")]
public static ResponseToolCapabilities GetToolCapabilities(
    string query = "",
    string status = "",
    string access = "",
    int limit = 50)
```

- `query`：按工具名/描述/Category 模糊匹配（空格分词，全部命中）。
- `status` / `access`：可选过滤，大小写不敏感；非法值返回空 + Message 提示合法值。
- `limit`：唯一语义 `<= 0 → 50`、`> 500 → 500`（`ToolCapabilityQuery.ClampLimit`）。
- 空结果消息：有 `query` 时用 `No tool matches query '...'`；只有过滤器（`query` 为空）时用过滤器专用提示，不能再说 `query ''`。
- `Total` 是过滤后、`limit` 截断前的命中数；`Items` 只是当前页。
- 实现必须调用 `ToolCapabilityQuery.Filter`，不要在这个文件里重复过滤逻辑。
- 返回类型：

```csharp
public sealed class ResponseToolCapabilities : ResponseMessage
{
    public string TiaMajorVersion { get; set; } = "";
    public string Profile { get; set; } = "";
    public int Total { get; set; }
    public List<ToolCapabilityInfo> Items { get; set; } = new();
}
```

- 工具目录来自 `AllToolMethods()`（与 FindTools 同一份）；描述用 `ToolDescription(m)`。
- `TiaMajorVersion` 用 `Engineering.TiaMajorVersion`；未知写 `"unknown"`。
- `Profile` 用 `ResolvedProfile()`。

同时修改 `FindTools`：在每行签名后追加 `"  [" + access + "|" + status + "]"`（小写），再追加原有的 `[already listed]`/`[call via CallTool]`。不改返回类型。

### 1.5 `ModelContextProtocol/McpServer.Batch.cs`

新增工具：

```csharp
[McpServerTool(Name = "PlanBatch")]
[Description("[L1][Orchestration] ...")]
public static ResponseBatchPlan PlanBatch(
    string operationsJson,
    bool confirm = false,
    bool allowDirectOnly = false,
    int maxOperations = 200)

[McpServerTool(Name = "RunBatch")]
[Description("[L1][Orchestration][Destructive] ...")]
public static async Task<ResponseBatchRun> RunBatch(
    IMcpServer server,
    RequestContext<CallToolRequestParams> context,
    string operationsJson,
    bool confirm = false,
    bool continueOnError = false,
    bool allowDirectOnly = false,
    int maxOperations = 200,
    CancellationToken cancellationToken = default)

[McpServerTool(Name = "GetOpsLedger")]
[Description("[L0][Meta] ...")]
public static ResponseStringList GetOpsLedger(int limit = 50)
```

- `operationsJson` 形如 `[{"tool":"GetBlocks","argumentsJson":"{\"softwarePath\":\"PLC_1\"}"}, ...]`。
- `PlanBatch`：
  - 解析 JSON 数组（非法 → `McpException InvalidParams`，消息含示例）。
  - 用 `BatchPlanner.Plan`；再用 `ReflectiveToolInvoker.Bind` 对每个 `WillRun` 的 op 做参数绑定预检（注入器提供 `null`/`CancellationToken.None` 占位；绑定错误追加 issue 并置 `WillRun=false`）。
  - 返回 `ResponseBatchPlan { Message, CanRun, MaxOperations, PlanIssues, Operations }`。
  - 不执行、不碰 Portal。
- `RunBatch`：
  - 先执行与 `PlanBatch` 相同的预检；`CanRun=false` → 抛 `McpException`，消息逐条列出 issues。
  - 生成 `batchId = "batch-" + Guid.NewGuid().ToString("N")[..12]`。
  - 用静态 `SemaphoreSlim(1,1)` 串行化整个批次（进程内 Portal 锁）；等待时尊重 `cancellationToken`。
  - 逐 op 调 `CallTool(server, context, op.Tool, op.ArgumentsJson)`；记录耗时、成功/异常。
  - 每个 op 追加一条 `OpsLedgerEntry`（成功 `ok`，异常 `error`，跳过 `skipped`）。
  - `continueOnError=false` 时首个失败即停，后续标 `skipped`。
  - 返回 `ResponseBatchRun { Message, BatchId, BatchStatus, RequestedCount, StartedCount, CompletedCount, SuccessCount, FailedCount, SkippedCount, Operations }`。
  - 整个批次自身是 `DirectOnly` + `Destructive`：`ToolSafety` 表要加 `RunBatch`；`OperationCanceledException` 不吞，转 `McpException` 或直接抛。
- `GetOpsLedger`：读 `OpsLedger.DefaultPath` 最后 N 行，返回 `ResponseStringList`。

`ResponseBatchPlan` / `ResponseBatchRun` / `ResponseBatchOperationResult` 放在 `Responses.cs` 或 `McpServer.Batch.cs` 内（后者更内聚；但 `ResponseBatchPlan` 被 `PlanBatch` 返回，放 `Responses.cs` 也行）。

### 1.6 ToolSafety / Profile 更新

- `ToolSafety.DirectOnly` 增加 `"RunBatch"`。
- `ToolSafety.PortalFree` 增加 `"GetToolCapabilities"`, `"PlanBatch"`, `"GetOpsLedger"`。
- `ToolSafety.ReadOnlyExtra` 增加 `"GetToolCapabilities"`, `"PlanBatch"`, `"GetOpsLedger"`（`RunBatch` 不是只读）。
- `McpServer.Profile.LiteToolNames` 增加：
  - `"GetToolCapabilities"`, `"PlanBatch"`, `"GetOpsLedger"`, `"RunBatch"`（RunBatch 必须按名列出，和 DirectOnly 一致）。
- 同步更新 `ToolSafetyTests` 的 portal-free 期望集合。

### 1.7 测试

新增：
- `tests/TiaMcpServer.Tests/ToolCapabilityTests.cs`
- `tests/TiaMcpServer.Tests/BatchPlannerTests.cs`
- `tests/TiaMcpServer.Tests/OpsLedgerTests.cs`

测试 csproj 链接新零依赖文件：
- `ToolCapability.cs`
- `BatchPlanner.cs`
- `OpsLedger.cs`
- （`ToolSafety.cs` 已链接）

`Program.cs` 增加三组 `Run(check, skip)`，放在 ToolSafety 之后、CLI 之前。

必须钉住：
- Category 解析（`[L2][Category:PLC-Online]` → `PLC-Online`）。
- Access：`GetBlocks`=Read，`SetPlcTag`=Write，`DeletePlcBlock`=Delete。
- Status：`GetBlocks`=Verified（Evidence 指回 2026-10-30 矩阵）；`GetBlockInterface`/`GetTypeStructure` 永远不是 Verified；`GetHmiScreens`=Unverified；V19 下 `ExportAsDocuments`=Planned，V21 下=Unverified；`ImportFromDocuments`/`ExportBlocksAsDocuments` 在 V20/V21=Verified、V19=Planned、未知=Unverified；`SyncVersionControlWorkspace` 在 V20=Planned、V21=Unverified，且 Requires 含 `V21+`。
- Verified / Unverified 两个显式名字集合不相交。
- Requires：`DownloadToPlc` 含 `portal/online/confirm`；`SetDeviceItemIoAddress` 含 `dryRun`；`GetToolCapabilities` 不含 `dryRun`；`DeletePlcExternalSource`/`ImportBlocksFromDirectory` 不含 `dryRun`（无该参数）。
- Query helper：非法 status/access、`limit=0`/`501`、多词查询、Category 命中、`Total` vs 页大小。
- BatchPlanner：空批、未知工具、Planned、DirectOnly、无 confirm 的写、非法 JSON、超过 200、合法批。
- OpsLedger：临时目录 append→read tail；哈希稳定；不写明文参数。
- ToolSafety：`RunBatch` 是 destructive + direct-only；新增 portal-free 工具不碰 Portal。

### 1.8 文档

- 新增 `docs/TRUST-LAYER.md`：解释 access/status/requires/evidence、批次预检、账本、示例 JSON。
- `README.V19.md`：工具数 235 → 239；新增一节 "v2.8.0 Trust & Batch"。
- `CHANGELOG.md`：顶部加 `## 2.8.0`（日期 2026-11-19），列出新增工具与行为。
- 运行 `python3 scripts/Generate-ToolsList.py` 更新 `manifest/tools-list.json`（如果脚本参数允许；不要手改大文件）。
- 更新 `docs/tool-capability-matrix.md` 可选；若脚本是 PowerShell 且 Linux 跑不了，跳过并在 CHANGELOG 说明。

---

## 2. 验收命令

```bash
# 构建（V19 参考 DLL 在本机）
dotnet build tools/tiaportal-mcp/src/TiaMcpServer/TiaMcpServer.V19.csproj -c Release \
  -p:SiemensEngineeringDirectory=/home/li/csharp-learning/port/siemens19/V19

# 离线测试（本机只有 .NET 10 运行时，必须 roll-forward）
DOTNET_ROLL_FORWARD=LatestMajor dotnet run --project tools/tiaportal-mcp/tests/TiaMcpServer.Tests -c Release
```

要求：构建 0 errors；测试 `0 failed`；新增用例全部通过；687 条基线 + 7 条终检补充用例
（账本脱敏/共享追加）共 698 条不回归。

---

## 3. 给实现的约束

- 不 `git commit`、不 `git push`、不建分支（父级统一提交）。
- 只改本规格列出的文件；不要重构无关代码。
- 所有新公开类型/方法要有 XML 注释或中文注释解释"为什么"。
- 错误消息用中文或英文均可，但必须包含：哪个操作、为什么失败、怎么恢复。
- 不要吞异常；`RunBatch` 的失败要保留原始异常消息。
- 不要记录明文参数到账本（只记 SHA-256）。

# Trust Layer — tool capability model

This document describes the v2.8.0 trust layer: the per-tool capability matrix
(`GetToolCapabilities`), batch pre-checking and execution (`PlanBatch` / `RunBatch`), and the
operation ledger (`GetOpsLedger`).

The goal is simple: before a model calls a tool it should be able to tell

- **what the call does** (`access`: read / write / delete),
- **how well this repository has verified it** (`status`),
- **what it needs first** (`requires`: a connected portal, a live CPU, confirmation, a
  dry run, or a newer TIA version), and
- **why it carries that status** (`evidence`).

The full contract lives in [`docs/TRUST-LAYER-SPEC.md`](TRUST-LAYER-SPEC.md).

## `GetToolCapabilities`

`GetToolCapabilities` is an `[L0][Meta]` tool available in every profile (lite and full).
It does not touch the TIA Portal handle, so it is safe to call at any point.

```
GetToolCapabilities(query = "", status = "", access = "", limit = 50)
```

| parameter | meaning |
| --- | --- |
| `query` | Space-separated words. Every word must occur in the tool name, its description or its category. Case-insensitive. Empty matches everything. |
| `status` | Optional filter: `Verified`, `Implemented`, `Unverified`, `Planned` (case-insensitive). An unknown value returns an empty list plus a message naming the valid values. |
| `access` | Optional filter: `Read`, `Write`, `Delete` (case-insensitive). Same invalid-value behaviour. |
| `limit` | Page size: values `<= 0` fall back to 50, values `> 500` are clamped to 500. `total` counts every match before this cut. |

The result is a `ResponseToolCapabilities`. This is the response to
`GetToolCapabilities(limit = 2)` — no query, so `total` is the whole roster and `items` is the
first page in name order:

```json
{
  "message": "Returned 2 of 239 matching tools (roster: 239 total, profile: lite).",
  "meta": { "timestamp": "2026-11-19T10:00:00", "success": true },
  "tiaMajorVersion": "19",
  "profile": "lite",
  "total": 239,
  "items": [
    {
      "name": "AddDevice",
      "category": "Hardware",
      "access": "Write",
      "status": "Implemented",
      "destructive": false,
      "directOnly": false,
      "openWorld": false,
      "requires": ["portal"],
      "evidence": ""
    },
    {
      "name": "AddDeviceWithFallback",
      "category": "Hardware",
      "access": "Write",
      "status": "Implemented",
      "destructive": false,
      "directOnly": false,
      "openWorld": false,
      "requires": ["portal"],
      "evidence": ""
    }
  ]
}
```

`total` is the number of tools matched **after filtering but before `limit`**; `items`
holds the current page. `tiaMajorVersion` is `"unknown"` when the server has not resolved
an engine version (it never guesses a version).

Enums are serialized as PascalCase strings: the SDK's `McpJsonUtilities.CreateDefaultOptions()`
registers a string enum converter, so `"access": "Write"` and `"status": "Verified"` are the
contract — not numbers.

## `FindTools` shows capabilities inline

`FindTools` keeps its existing response type, but every signature line now carries a short
capability tag before the existing reachability marker:

```
GetBlocks(softwarePath: string, ...)  [read|verified]  [already listed - call it directly]
ExportAsDocuments(...)                [write|planned]  [call via CallTool]
```

That lets a model decide whether a tool is the right one *before* paying for a second
round trip to `GetToolCapabilities`.

## Fields

### `access`

| value | meaning |
| --- | --- |
| `Read` | `ToolSafety.Classify(name).ReadOnly` is true. The call only reads. |
| `Delete` | Not read-only, and the name starts with `Delete`. |
| `Write` | Everything else. It may change the project, a file, or a CPU. |

`access != Read` is the signal that a host should ask the user before running the tool.

### `status`

| value | meaning |
| --- | --- |
| `Verified` | On the explicit `VerifiedTools` list, and every entry carries its **own** evidence string. There is no shared "verified" sentence. |
| `Implemented` | The tool exists and follows the normal rules, but there is no recorded real-machine run. Treat it as usable but unproven. |
| `Unverified` | Explicitly known to be untested here: the whole HMI family (any name containing `Hmi`), `GetBlockInterface` / `GetTypeStructure` (README.V19 2026-11-18 says they were not real-machine tested), the VCI tools at V21, and the SD document tools without a live run (`ExportAsDocuments`, `ImportBlocksFromDocuments`). |
| `Planned` | Version-gated and **not supported by the current engine**. The four SD document tools are `Planned` below V20 (evidence `SIMATIC SD requires V20+`); the VCI tools (`GetVersionControlWorkspaces`, `CreateVersionControlWorkspace`, `GetVersionControlStatus`, `SyncVersionControlWorkspace`, `ConnectProjectToWorkspace`) are `Planned` below V21 (evidence `Version control requires V21+`). |

The verified evidence groups are:

- `README.V19 真机矩阵 2026-10-30` — the core PLC block/tag/compile/export/save paths.
- `README.V19 真机矩阵 2026-10-30（改名除外，见已知问题 2）` — `SetPlcTag`.
- `docs/在线实时读值_使用指南.md` — the two runtime live-value readers. Each has its own
  string: `docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，S7 M/I/Q + 非优化 DB）`
  for `ReadPlcLiveValuesS7`, and
  `docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，OPC UA 节点）` for
  `ReadPlcLiveValuesOpcUa`. The matrix above does not list them; §八 of that guide is the
  per-tool record (read-only on a real CPU).
- `CHANGELOG 2.2.5 / 2.2.7 V21 真机` — `DownloadToPlc`, `ConnectIsolated`.
- `CHANGELOG 2.2.9 V20/V21 活工程实测` — `ImportFromDocuments`, `ExportBlocksAsDocuments`
  (reported `Verified` only when the V20+ gate passes; `Planned` on V19, `Unverified` when
  the engine version is unknown).

A tool that is not in one of those groups is never `Verified`, even if it was exercised in
an older release without a citable per-tool record.

`Verified` takes precedence over `Unverified`, but not over `Planned`; the explicit verified
and unverified name sets are disjoint (pinned by a test). When the engine version is unknown
(`tiaMajorVersion == 0`) no tool is reported as `Planned`, because the server cannot honestly
claim the current engine is too old.

### `requires`

A fixed-order, de-duplicated list. Empty means "no extra prerequisite".

| value | when |
| --- | --- |
| `portal` | The call uses the process-wide Openness handle (`ToolSafety.TouchesPortal`). It must not overlap another portal call. |
| `online` | The call talks to a CPU / OPC UA server / PLCSIM (`ToolSafety.OpenWorld`). |
| `confirm` | The call is destructive (`ToolSafety.Destructive`); a host should ask the user first. |
| `dryRun` | The tool name is in the explicit `DryRunTools` set (it declares a real `bool dryRun` parameter). Description text alone never adds this prerequisite: `GetToolCapabilities` mentions the word but does not require it. |
| `V20+` | The tool is one of the SIMATIC SD document tools; it needs TIA Portal V20 or newer. |
| `V21+` | The tool is a version-control tool (`*VersionControl*` or `ConnectProjectToWorkspace`); it needs TIA Portal V21 or newer. |

Order is always `portal`, `online`, `confirm`, `dryRun`, `V20+`, `V21+` so text output is
stable and diffable.

### `destructive` / `directOnly` / `openWorld`

Taken verbatim from `ToolSafety.Classify(name)`. In particular, `directOnly` tools
(delete/download/online) are refused through `CallTool` and are always listed directly in
the lite profile so the host's approval prompt names them.

### `evidence`

A short, human-readable reason for the status. It is empty for `Implemented` and
`Unverified` (there is nothing to cite), it names the exact document/version for
`Verified`, and the version gate for `Planned`. Each `Verified` tool has its own string:
`README.V19 真机矩阵 2026-10-30`, `README.V19 真机矩阵 2026-10-30（改名除外，见已知问题 2）`,
`docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，S7 M/I/Q + 非优化 DB）`,
`docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，OPC UA 节点）`,
`CHANGELOG 2.2.5 / 2.2.7 V21 真机`, or `CHANGELOG 2.2.9 V20/V21 活工程实测`.

## Where the classification lives

- `ModelContextProtocol/ToolCapability.cs` — zero-dependency classifier
  (`ToolCapabilities.Classify` / `All`) plus the pure `ToolCapabilityQuery` filter/limit
  helper. It only reads `ToolSafety` and the `[Lx][Category]` description prefix, so the
  offline suite can link and pin it.
- `ModelContextProtocol/McpServer.Capabilities.cs` — the `GetToolCapabilities` tool; all
  filtering, ordering and limit clamping is delegated to `ToolCapabilityQuery`.
- `ModelContextProtocol/ToolSafety.cs` — the single safety table. `GetToolCapabilities` is
  `PortalFree` and read-only.
- `tests/TiaMcpServer.Tests/ToolCapabilityTests.cs` — pins category parsing, access,
  per-tool evidence, the VCI names and version gate, the SD verified/unverified split,
  the explicit `dryRun` set, the query/filter/limit boundaries, and the portal-free flag.

## Batches: `PlanBatch` and `RunBatch`

A batch is a JSON array of `{tool, argumentsJson}` objects, executed in array order:

```json
[
  {"tool": "GetBlocks",  "argumentsJson": "{\"softwarePath\":\"PLC_1\"}"},
  {"tool": "SetPlcTag",  "argumentsJson": "{\"softwarePath\":\"PLC_1\",\"tagName\":\"Tag_1\",\"tagTableName\":\"Default tag table\",\"dataType\":\"Int\",\"address\":\"%MW100\"}"}
]
```

### `PlanBatch` — validate without running

```
PlanBatch(operationsJson, confirm = false, allowDirectOnly = false, maxOperations = 200)
```

`PlanBatch` is `[L1][Orchestration]`, read-only, and portal-free. It applies the same rules
as `RunBatch` and returns `ResponseBatchPlan`:

```json
{
  "message": "Batch cannot run: 1 issue(s). Fix them and call PlanBatch again. operation 1: 'SetPlcTag' writes; confirm=true is required",
  "canRun": false,
  "maxOperations": 200,
  "planIssues": [],
  "operations": [
    {
      "index": 0,
      "tool": "GetBlocks",
      "access": "Read",
      "status": "Verified",
      "destructive": false,
      "directOnly": false,
      "known": true,
      "willRun": true,
      "issues": []
    },
    {
      "index": 1,
      "tool": "SetPlcTag",
      "access": "Write",
      "status": "Verified",
      "destructive": true,
      "directOnly": false,
      "known": true,
      "willRun": false,
      "issues": ["operation 1: 'SetPlcTag' writes; confirm=true is required"]
    }
  ],
  "meta": { "timestamp": "2026-11-19T10:00:00", "success": true }
}
```

Rules (in order, all collected in one pass):

- empty batch → plan issue `batch is empty`;
- more than `maxOperations` (default 200, clamped to 1..200) → plan issue naming the limit;
- empty tool → `operation i: tool is required`;
- unknown tool → `operation i: unknown tool 'X'`;
- `status == Planned` → `operation i: 'X' is not supported on this TIA version (planned)`;
- direct-only tool without `allowDirectOnly=true` → `operation i: 'X' must be called by its own name; ...`;
- `access != Read` without `confirm=true` → `operation i: 'X' writes; confirm=true is required`;
- nested `RunBatch` → `operation i: RunBatch cannot be nested inside a batch` (the process-wide
  portal lock is not reentrant, so allowing it would deadlock the batch);
- nested `CallTool` → `operation i: CallTool cannot be nested; name the target tool directly`
  (it is only another entrance to the same executor);
- `InvokeObject` / `InvokeService` with `allowWrite=true` → `operation i: 'X' with
  allowWrite=true cannot be nested; call it by its own name so the host names it` (same reason
  `CallTool` refuses it: it can reach any public Openness method);
- non-empty `argumentsJson` that is not a JSON object → `operation i: argumentsJson must be a JSON object`.

Each operation also carries `known`. It is `true` only when the tool was found. When it is
`false`, `access` / `status` still show their enum defaults (`Read` / `Verified`) simply because
those are the zero values — they must not be read as a capability claim.

After the static rules, `PlanBatch` binds every would-run operation with
`ReflectiveToolInvoker.Bind` against the real tool method. A missing or unknown parameter
(for example `software` instead of `softwarePath`) is appended to that operation's `issues`,
`willRun` becomes false, and `canRun` is recomputed. Nothing is executed and the TIA Portal
handle is never touched.

### `RunBatch` — execute behind the portal lock

```
RunBatch(operationsJson, confirm = false, continueOnError = false,
         allowDirectOnly = false, maxOperations = 200)
```

`RunBatch` is `[L1][Orchestration][Destructive]` and `DirectOnly`: the host must list it by
name (it is in the lite roster), so its approval prompt names the batch rather than an opaque
`CallTool`. It:

1. re-runs the exact `PlanBatch` preflight — if `canRun` is false it throws `McpException`
   and **nothing** runs (a half-executed batch is harder to recover from than a whole one);
2. creates `batchId = "batch-" + <12 hex>`;
3. acquires one process-wide `SemaphoreSlim(1,1)` for the whole batch (waiting honours the
   cancellation token), because the Openness handle is a process singleton;
4. calls each tool through the same executor `CallTool` uses
   (`ExecuteToolByName`), sequentially, measuring the duration;
5. writes one ledger line per operation (`ok` / `error` / `skipped`).

`continueOnError=false` (the default) stops at the first failure and marks every remaining
operation `skipped`. With `continueOnError=true` the batch keeps going. Cancellation is not
swallowed: the in-flight operation's ledger line is written first, then the
`OperationCanceledException` propagates.

`allowDirectOnly=true` is the only way to batch a download/online/delete tool. It is an
explicit opt-in because the host's approval prompt only names `RunBatch`; the public
`CallTool` keeps refusing those tools regardless.

The response carries `batchId`, `batchStatus` (`ok` / `partial` / `failed`),
`requestedCount`, `startedCount`, `completedCount`, `successCount`, `failedCount`,
`skippedCount`, and one `{index, tool, status, durationMs, error}` per operation.
There is no `cancelled` status: cancellation is not a batch result. The in-flight operation's
ledger line (`status: "error"`, `error: "cancelled"`) is written first, then the
`OperationCanceledException` propagates to the host, so a cancelled batch never returns a
half-true result object.

## `GetOpsLedger` and the ledger file

```
GetOpsLedger(limit = 50)
```

`GetOpsLedger` is `[L0][Meta]`, read-only and portal-free. It returns the last `limit` lines
of the JSONL ledger (`limit <= 0` → 50, `> 500` → 500) as `ResponseStringList.items`.

Ledger path: `TIA_MCP_LEDGER` when set, otherwise
`%LOCALAPPDATA%\TiaMcpServer\ops-ledger.jsonl` (on Unix,
`~/.local/share/TiaMcpServer/ops-ledger.jsonl`). One line per operation:

```json
{"timestamp":"2026-11-19T10:00:00.0000000+08:00","batchId":"batch-0123456789ab","index":0,"tool":"GetBlocks","access":"Read","status":"ok","durationMs":42,"argumentsSha256":"<64 lowercase hex>","error":null}
```

- `argumentsSha256` is the SHA-256 of the raw `argumentsJson` string, lowercase hex.
  **Plain-text arguments are never written**: projects contain customer names, addresses
  and tag values, and the ledger may outlive the session.
- `status` is one of `ok`, `error`, `skipped`.
- `error` is redacted before it is written: tool exceptions quote the tag names, paths and
  values the caller passed, so **every scalar value** found in `argumentsJson` — strings,
  numbers and `true`/`false`, nested arrays and objects included — is replaced with `***`
  (`OpsLedger.RedactError`). Longest values are replaced first, and replacement happens
  **before** truncation, so a value that straddles the 300-character boundary cannot survive
  as a plaintext prefix and a one-character value that expands to `***` cannot push the
  result past the cap. If the arguments are not valid JSON, only the truncation applies. The
  exception type and message shape are preserved.
- Appends and reads share a process-wide lock; appends open the file with
  `FileShare.ReadWrite` so a second process (another host, a log shipper) can append too. A
  missing file reads as an empty list.

## Where batch code lives

- `ModelContextProtocol/BatchPlanner.cs` — zero-dependency static rules (`BatchPlanner.Plan`).
- `ModelContextProtocol/OpsLedger.cs` — zero-dependency JSONL append/read and `HashArguments`.
- `ModelContextProtocol/McpServer.Batch.cs` — `PlanBatch` / `RunBatch` / `GetOpsLedger`, the
  binding preflight, the portal semaphore, and the response types.
- `ModelContextProtocol/McpServer.ToolBridge.cs` — `CallTool` and the shared
  `ExecuteToolByName` executor (`allowDirectOnly` is false on the public path).
- `tests/TiaMcpServer.Tests/BatchPlannerTests.cs` / `OpsLedgerTests.cs` — offline pins.

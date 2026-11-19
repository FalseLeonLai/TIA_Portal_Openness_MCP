using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>One operation's execution outcome inside a RunBatch response. Error is only
    /// set for status "error"; skipped operations keep it null so a caller can tell "did not
    /// run because an earlier one failed" from "ran and failed".</summary>
    public sealed class ResponseBatchOperationResult
    {
        public int Index { get; set; }
        public string Tool { get; set; } = "";
        /// <summary>ok / error / skipped</summary>
        public string Status { get; set; } = "";
        public long DurationMs { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>The read-only result of PlanBatch. Nothing here has executed: CanRun is the
    /// only bit RunBatch will trust, and every reason it is false is attached to the operation
    /// or to the plan itself.</summary>
    public sealed class ResponseBatchPlan : ResponseMessage
    {
        public bool CanRun { get; set; }
        public int MaxOperations { get; set; }
        public string[] PlanIssues { get; set; } = Array.Empty<string>();
        public List<BatchOperationPlan> Operations { get; set; } = new List<BatchOperationPlan>();
    }

    /// <summary>The result of a batch that did pass preflight. BatchStatus is ok / partial /
    /// failed; counts always satisfy requested = success + failed + skipped and
    /// started = success + failed. Cancellation never produces this object: the in-flight
    /// ledger line is written, then the OperationCanceledException propagates to the host.</summary>
    public sealed class ResponseBatchRun : ResponseMessage
    {
        public string BatchId { get; set; } = "";
        public string BatchStatus { get; set; } = "";
        public int RequestedCount { get; set; }
        public int StartedCount { get; set; }
        public int CompletedCount { get; set; }
        public int SuccessCount { get; set; }
        public int FailedCount { get; set; }
        public int SkippedCount { get; set; }
        public List<ResponseBatchOperationResult> Operations { get; set; } = new List<ResponseBatchOperationResult>();
    }

    /// <summary>
    /// Batch orchestration on top of the same safety/capability tables as every other tool:
    /// PlanBatch validates statically (including argument binding) without touching the
    /// portal, RunBatch executes sequentially behind one process-wide lock and records every
    /// operation in the JSONL ledger, GetOpsLedger reads that ledger back.
    ///
    /// 为什么整批只拿一把锁：TIA Portal 的 Openness 句柄是进程级单例，两个"看起来独立"的
    /// 工具在同一个句柄上交错，正是那些最难复现的崩溃和「对象已失效」的来源。串行是慢一点，
    /// 但批次本来就是按顺序表达意图的。
    /// </summary>
    public static partial class McpServer
    {
        /// <summary>Process-wide serialization for batch execution. A semaphore rather than a
        /// lock because the batch body awaits tool calls; waiting honors cancellation.</summary>
        private static readonly SemaphoreSlim BatchGate = new SemaphoreSlim(1, 1);

        /// <summary>Binding preflight has no server/request to inject, but the tools that need
        /// those parameters must still bind. Returning true with null is exactly what the real
        /// injector does for a null server; value types are unaffected.</summary>
        private static readonly ReflectiveToolInvoker.Injector PreflightInjector = PreflightInject;

        private static bool PreflightInject(Type parameterType, out object? value)
        {
            value = null;
            return parameterType == typeof(IMcpServer)
                || parameterType == typeof(RequestContext<CallToolRequestParams>);
        }

        [McpServerTool(Name = "PlanBatch"), Description(
            "[L1][Orchestration] Validate a list of tool calls WITHOUT executing anything. " +
            "operationsJson is a JSON array like " +
            "'[{\"tool\":\"GetBlocks\",\"argumentsJson\":\"{\\\"softwarePath\\\":\\\"PLC_1\\\"}\"}]'. " +
            "Checks that every tool exists, is supported by this TIA version, is not direct-only unless allowDirectOnly=true, " +
            "has confirm=true when it writes, and that argumentsJson binds to the tool's real parameters. " +
            "Returns CanRun plus every issue per operation. Does not touch TIA Portal. " +
            "USE THIS before RunBatch: it costs nothing and turns a half-executed batch into a list of fixes.")]
        public static ResponseBatchPlan PlanBatch(
            [Description("operationsJson: JSON array of {tool, argumentsJson} objects, executed in array order.")] string operationsJson,
            [Description("confirm: true is required for operations that write or delete.")] bool confirm = false,
            [Description("allowDirectOnly: allow download/online/delete tools in the batch. Their host approval prompt only names RunBatch, so use sparingly.")] bool allowDirectOnly = false,
            [Description("maxOperations: batch size limit, default 200; values below 1 become 1 and above 200 become 200.")] int maxOperations = BatchPlanner.DefaultMaxOperations)
        {
            List<BatchOperationInput> inputs;
            BatchPlan plan;
            try
            {
                inputs = ParseBatch(operationsJson);
                plan = BuildPlan(inputs, confirm, allowDirectOnly, maxOperations);
            }
            catch (McpException)
            {
                // Invalid JSON is InvalidParams; the SDK turns it into a proper MCP error
                // instead of a "successful" response that only contains a complaint.
                throw;
            }
            catch (Exception ex)
            {
                return new ResponseBatchPlan
                {
                    Message = "PlanBatch failed: " + ex.Message,
                    CanRun = false,
                    MaxOperations = BatchPlanner.ClampMaxOperations(maxOperations),
                    PlanIssues = new[] { ex.Message },
                    Meta = BridgeMeta(false),
                };
            }

            string message;
            if (plan.CanRun)
            {
                message = plan.Operations.Count + " operation(s) can run. Call RunBatch with the same operationsJson"
                          + (NeedsConfirm(plan) ? " and confirm=true" : "") + ".";
            }
            else
            {
                var issues = CollectIssues(plan);
                message = "Batch cannot run: " + issues.Count + " issue(s). Fix them and call PlanBatch again. "
                          + string.Join(" | ", issues);
            }

            return new ResponseBatchPlan
            {
                Message = message,
                CanRun = plan.CanRun,
                MaxOperations = plan.MaxOperations,
                PlanIssues = plan.PlanIssues,
                Operations = plan.Operations,
                Meta = BridgeMeta(true),
            };
        }

        [McpServerTool(Name = "RunBatch"), Description(
            "[L1][Orchestration][Destructive] Execute a pre-checked batch of tool calls sequentially. " +
            "operationsJson is the same JSON array as PlanBatch " +
            "'[{\"tool\":\"GetBlocks\",\"argumentsJson\":\"{\\\"softwarePath\\\":\\\"PLC_1\\\"}\"}]'. " +
            "The whole batch is re-preflighted first and is refused (nothing runs) if any operation has an issue. " +
            "Operations run in array order behind one process-wide Portal lock; every operation is written to the JSONL " +
            "operation ledger (arguments are stored only as SHA-256). confirm=true is required for writes/deletes; " +
            "continueOnError=false stops at the first failure and marks the rest skipped; " +
            "allowDirectOnly=true permits download/online/delete tools in the batch. " +
            "Use PlanBatch first to see the issues without running anything.")]
        public static async Task<ResponseBatchRun> RunBatch(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            [Description("operationsJson: JSON array of {tool, argumentsJson} objects, executed in array order.")] string operationsJson,
            [Description("confirm: true is required for operations that write or delete.")] bool confirm = false,
            [Description("continueOnError: false (default) stops at the first failure and marks the rest skipped; true keeps going.")] bool continueOnError = false,
            [Description("allowDirectOnly: allow download/online/delete tools in the batch. Their host approval prompt only names RunBatch, so use sparingly.")] bool allowDirectOnly = false,
            [Description("maxOperations: batch size limit, default 200; values below 1 become 1 and above 200 become 200.")] int maxOperations = BatchPlanner.DefaultMaxOperations,
            CancellationToken cancellationToken = default)
        {
            var inputs = ParseBatch(operationsJson);
            var plan = BuildPlan(inputs, confirm, allowDirectOnly, maxOperations);

            // 预检失败时一条都不跑。半执行的批次比整批拒绝更难恢复：调用方无法从
            // 「前三条改了工程、第四条没改」推断出工程现在处于什么状态。
            if (!plan.CanRun)
                throw new McpException(
                    "RunBatch refused: the batch did not pass preflight, nothing was executed. "
                    + string.Join(" | ", CollectIssues(plan))
                    + " Call PlanBatch with the same operationsJson to inspect the same issues without running anything.",
                    McpErrorCode.InvalidParams);

            string batchId = "batch-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string ledgerPath = OpsLedger.DefaultPath;

            await BatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ExecuteBatch(server, context, plan, inputs, batchId, ledgerPath, continueOnError, allowDirectOnly, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                BatchGate.Release();
            }
        }

        [McpServerTool(Name = "GetOpsLedger"), Description(
            "[L0][Meta] Read the tail of the operation ledger written by RunBatch. " +
            "Returns JSONL lines with timestamp, batchId, index, tool, access, status (ok/error/skipped), " +
            "durationMs, argumentsSha256 and error. Arguments are never stored in plain text. " +
            "Default limit 50 (values <= 0 use 50, values > 500 are capped at 500). " +
            "Does not touch TIA Portal. Use it to audit what a batch did or to resume after a failure.")]
        public static ResponseStringList GetOpsLedger(
            [Description("limit: number of trailing ledger lines to return, default 50; values <= 0 use 50, values > 500 are capped at 500.")] int limit = OpsLedger.DefaultLimit)
        {
            string path = OpsLedger.DefaultPath;
            try
            {
                var lines = OpsLedger.ReadTail(path, limit);
                string message = lines.Count == 0
                    ? "The operation ledger is empty or does not exist yet: " + path
                    : "Returned the last " + lines.Count + " ledger line(s) from " + path + ".";
                return new ResponseStringList
                {
                    Message = message,
                    Items = lines,
                    Meta = BridgeMeta(true),
                };
            }
            catch (Exception ex)
            {
                return new ResponseStringList
                {
                    Message = "GetOpsLedger failed for " + path + ": " + ex.Message,
                    Items = Array.Empty<string>(),
                    Meta = BridgeMeta(false),
                };
            }
        }

        private static async Task<ResponseBatchRun> ExecuteBatch(
            IMcpServer server,
            RequestContext<CallToolRequestParams> context,
            BatchPlan plan,
            IReadOnlyList<BatchOperationInput> inputs,
            string batchId,
            string ledgerPath,
            bool continueOnError,
            bool allowDirectOnly,
            CancellationToken cancellationToken)
        {
            int requested = plan.Operations.Count;
            int started = 0;
            int completed = 0;
            int success = 0;
            int failed = 0;
            int skipped = 0;
            bool stop = false;

            var results = new List<ResponseBatchOperationResult>(requested);
            var ledgerErrors = new List<string>();

            for (int i = 0; i < plan.Operations.Count; i++)
            {
                var operation = plan.Operations[i];
                string argumentsJson = inputs[i].ArgumentsJson ?? "";

                if (stop)
                {
                    // Skipped operations are still recorded: "the batch stopped before this
                    // one" is part of the audit trail, not an absence of data.
                    skipped++;
                    results.Add(new ResponseBatchOperationResult
                    {
                        Index = operation.Index,
                        Tool = operation.Tool,
                        Status = "skipped",
                    });
                    WriteLedger(ledgerPath, ledgerErrors, batchId, operation, "skipped", 0, argumentsJson, null);
                    continue;
                }

                // 在真正开始前尊重取消：已经开始的工具让它们自己观察 token（注入的就是
                // 这一个），还没开始的绝不偷偷再跑一条。
                cancellationToken.ThrowIfCancellationRequested();

                started++;
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    // Same executor as CallTool, but allowDirectOnly was already checked by the
                    // planner, so a direct-only operation can run here without weakening the
                    // public CallTool path.
                    await ExecuteToolByName(server, context, operation.Tool, argumentsJson, cancellationToken, allowDirectOnly)
                        .ConfigureAwait(false);

                    stopwatch.Stop();
                    completed++;
                    success++;
                    results.Add(new ResponseBatchOperationResult
                    {
                        Index = operation.Index,
                        Tool = operation.Tool,
                        Status = "ok",
                        DurationMs = stopwatch.ElapsedMilliseconds,
                    });
                    WriteLedger(ledgerPath, ledgerErrors, batchId, operation, "ok", stopwatch.ElapsedMilliseconds, argumentsJson, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 不吞取消：先尽力留下 in-flight 操作的账本行，再把原始异常交给宿主。
                    stopwatch.Stop();
                    WriteLedger(ledgerPath, ledgerErrors, batchId, operation, "error", stopwatch.ElapsedMilliseconds,
                        argumentsJson, "cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    completed++;
                    failed++;
                    results.Add(new ResponseBatchOperationResult
                    {
                        Index = operation.Index,
                        Tool = operation.Tool,
                        Status = "error",
                        DurationMs = stopwatch.ElapsedMilliseconds,
                        Error = ex.Message,
                    });
                    // 异常消息经常原样引用调用方传入的变量名/路径；账本只保留失败形状，
                    // 不再把明文参数落盘（参数本身已经只存 SHA-256）。
                    WriteLedger(ledgerPath, ledgerErrors, batchId, operation, "error", stopwatch.ElapsedMilliseconds,
                        argumentsJson, OpsLedger.RedactError(ex.Message, argumentsJson));
                    if (!continueOnError) stop = true;
                }
            }

            string batchStatus;
            if (failed == 0 && skipped == 0) batchStatus = "ok";
            else if (success == 0) batchStatus = "failed";
            else batchStatus = "partial";

            string message = "Batch " + batchId + ": " + success + "/" + requested + " succeeded"
                             + (failed > 0 ? ", " + failed + " failed" : "")
                             + (skipped > 0 ? ", " + skipped + " skipped" : "")
                             + ". Ledger: " + ledgerPath + ".";
            if (ledgerErrors.Count > 0)
                message += " Ledger write failed: " + string.Join(" | ", ledgerErrors) + ".";

            return new ResponseBatchRun
            {
                Message = message,
                BatchId = batchId,
                BatchStatus = batchStatus,
                RequestedCount = requested,
                StartedCount = started,
                CompletedCount = completed,
                SuccessCount = success,
                FailedCount = failed,
                SkippedCount = skipped,
                Operations = results,
                Meta = BridgeMeta(failed == 0),
            };
        }

        /// <summary>Ledger writes must never turn a completed engineering operation into a
        /// failed one, but they must not be invisible either: the batch message reports them.</summary>
        private static void WriteLedger(
            string path,
            List<string> errors,
            string batchId,
            BatchOperationPlan operation,
            string status,
            long durationMs,
            string argumentsJson,
            string? error)
        {
            try
            {
                OpsLedger.Append(path, new OpsLedgerEntry
                {
                    Timestamp = DateTime.Now.ToString("O", CultureInfo.InvariantCulture),
                    BatchId = batchId,
                    Index = operation.Index,
                    Tool = operation.Tool,
                    Access = operation.Access.ToString(),
                    Status = status,
                    DurationMs = durationMs,
                    ArgumentsSha256 = OpsLedger.HashArguments(argumentsJson),
                    Error = error,
                });
            }
            catch (Exception ex)
            {
                if (errors.Count < 3) errors.Add(ex.Message);
            }
        }

        /// <summary>Parse the batch array. Anything other than a JSON array of objects is an
        /// InvalidParams error naming the expected shape, because the model usually just sent
        /// the wrong nesting level.</summary>
        private static List<BatchOperationInput> ParseBatch(string operationsJson)
        {
            const string example =
                "[{\"tool\":\"GetBlocks\",\"argumentsJson\":\"{\\\"softwarePath\\\":\\\"PLC_1\\\"}\"}]";

            if (string.IsNullOrWhiteSpace(operationsJson))
                throw new McpException(
                    "PlanBatch/RunBatch: 'operationsJson' is required and must be a JSON array. Example: " + example,
                    McpErrorCode.InvalidParams);

            try
            {
                var parsed = JsonSerializer.Deserialize<List<BatchOperationInput>>(operationsJson, BridgeJson);
                if (parsed == null)
                    throw new McpException(
                        "PlanBatch/RunBatch: 'operationsJson' must be a JSON array, not null. Example: " + example,
                        McpErrorCode.InvalidParams);

                var inputs = new List<BatchOperationInput>(parsed.Count);
                foreach (var item in parsed)
                    inputs.Add(item ?? new BatchOperationInput());
                return inputs;
            }
            catch (JsonException ex)
            {
                throw new McpException(
                    "operationsJson is not a valid JSON array (" + ex.Message + "). It must look like " + example,
                    McpErrorCode.InvalidParams);
            }
        }

        /// <summary>Static rules first, then real argument binding. Binding is the half that
        /// catches "softwarePath" vs "software" before RunBatch has changed anything.</summary>
        private static BatchPlan BuildPlan(
            IReadOnlyList<BatchOperationInput> inputs,
            bool confirm,
            bool allowDirectOnly,
            int maxOperations)
        {
            var plan = BatchPlanner.Plan(inputs, LookupCapability, confirm, allowDirectOnly, maxOperations);
            var all = AllToolMethods();

            for (int i = 0; i < plan.Operations.Count; i++)
            {
                var operation = plan.Operations[i];

                // Use the canonical casing for everything afterwards (ledger, results, calls):
                // a model that sent "getblocks" should not produce a ledger that says getblocks.
                if (!string.IsNullOrWhiteSpace(operation.Tool) && all.TryGetValue(operation.Tool, out var known))
                    operation.Tool = known.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? known.Name;

                if (!operation.WillRun) continue;
                if (!all.TryGetValue(operation.Tool, out var method))
                {
                    AddIssue(operation, "operation " + operation.Index + ": unknown tool '" + operation.Tool + "'");
                    continue;
                }

                var args = ParseArgumentsObject(inputs[i].ArgumentsJson);
                var binding = ReflectiveToolInvoker.Bind(operation.Tool, method, args, PreflightInjector, BridgeJson);
                if (binding.Error != null)
                    AddIssue(operation, binding.Error);
            }

            plan.CanRun = plan.PlanIssues.Length == 0 && AllWillRun(plan.Operations);
            return plan;
        }

        private static ToolCapabilityInfo? LookupCapability(string name)
        {
            if (!AllToolMethods().TryGetValue(name, out var method)) return null;
            string canonical = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
            return ToolCapabilities.Classify(canonical, ToolDescription(method), Engineering.TiaMajorVersion);
        }

        private static JsonObject ParseArgumentsObject(string? argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson!.Trim() == "{}")
                return new JsonObject();
            // BatchPlanner already rejected non-object JSON, so this cast is safe; the fallback
            // keeps a defensive empty object instead of a NullReference if the two ever drift.
            return JsonNode.Parse(argumentsJson!) as JsonObject ?? new JsonObject();
        }

        private static void AddIssue(BatchOperationPlan operation, string issue)
        {
            var issues = new List<string>(operation.Issues) { issue };
            operation.Issues = issues.ToArray();
            operation.WillRun = false;
        }

        private static List<string> CollectIssues(BatchPlan plan)
        {
            var issues = new List<string>(plan.PlanIssues);
            for (int i = 0; i < plan.Operations.Count; i++)
            {
                if (plan.Operations[i].WillRun) continue;
                issues.AddRange(plan.Operations[i].Issues);
            }
            return issues;
        }

        private static bool AllWillRun(List<BatchOperationPlan> operations)
        {
            for (int i = 0; i < operations.Count; i++)
                if (!operations[i].WillRun) return false;
            return true;
        }

        private static bool NeedsConfirm(BatchPlan plan)
        {
            for (int i = 0; i < plan.Operations.Count; i++)
                if (plan.Operations[i].Access != ToolAccess.Read) return true;
            return false;
        }
    }
}

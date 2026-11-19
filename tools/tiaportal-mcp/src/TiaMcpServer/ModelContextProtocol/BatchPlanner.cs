using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>One requested tool call inside a batch, exactly as the caller sent it.</summary>
    public sealed class BatchOperationInput
    {
        public string Tool { get; set; } = "";
        public string ArgumentsJson { get; set; } = "";
    }

    /// <summary>
    /// One operation after static pre-checking. Issues is never null and carries every reason
    /// the operation may not run, so the caller can fix them all in one pass instead of
    /// resubmitting after each single error.
    /// </summary>
    public sealed class BatchOperationPlan
    {
        public int Index { get; set; }
        public string Tool { get; set; } = "";
        public ToolAccess Access { get; set; }
        public ToolStatus Status { get; set; }
        public bool Destructive { get; set; }
        public bool DirectOnly { get; set; }
        /// <summary>
        /// True only when the lookup found a real capability. When false, Access/Status keep
        /// their enum defaults (Read/Verified) purely because those are the zero values; they
        /// are meaningless. Consumers must check this before trusting access/status, otherwise
        /// an unknown tool silently looks like a verified read.
        /// </summary>
        public bool Known { get; set; }
        public bool WillRun { get; set; }
        public string[] Issues { get; set; } = Array.Empty<string>();
    }

    /// <summary>The whole pre-check result. CanRun is the single bit RunBatch trusts.</summary>
    public sealed class BatchPlan
    {
        public bool CanRun { get; set; }
        public int MaxOperations { get; set; }
        public string[] PlanIssues { get; set; } = Array.Empty<string>();
        public List<BatchOperationPlan> Operations { get; set; } = new List<BatchOperationPlan>();
    }

    /// <summary>
    /// Pure, zero-dependency static pre-checks for a batch: tool exists, version support,
    /// direct-only policy, confirmation and JSON shape. The adapter layer adds argument
    /// binding via ReflectiveToolInvoker; keeping the rules here lets the offline suite pin
    /// them without the MCP SDK or a TIA installation.
    ///
    /// 为什么值得单独抽出来：预检判错了不会崩 —— 它只会让一次「本来能跑」的批次被拒，
    /// 或者更糟，让一次「不该跑」的写入混进 RunBatch。只有会失败的用例盯得住这种表。
    /// </summary>
    public static class BatchPlanner
    {
        public const int DefaultMaxOperations = 200;

        /// <summary>The hard ceiling is also the default. A caller that passes 0 or a negative
        /// number gets 1 (not "unlimited"): an accidental zero must never turn into a full roster run.</summary>
        private const int MinMaxOperations = 1;

        public static BatchPlan Plan(
            IReadOnlyList<BatchOperationInput>? operations,
            Func<string, ToolCapabilityInfo?>? lookup,
            bool confirm,
            bool allowDirectOnly,
            int maxOperations = DefaultMaxOperations)
        {
            int limit = ClampMaxOperations(maxOperations);
            var plan = new BatchPlan { MaxOperations = limit };

            // 空批次不是「跑成功 0 条」：它几乎总是上游拼错了参数，必须明确拒绝。
            if (operations == null || operations.Count == 0)
            {
                plan.PlanIssues = new[] { "batch is empty" };
                plan.CanRun = false;
                return plan;
            }

            if (operations.Count > limit)
                plan.PlanIssues = new[] { "batch has " + operations.Count + " operations; the limit is " + limit };

            for (int i = 0; i < operations.Count; i++)
            {
                var input = operations[i] ?? new BatchOperationInput();
                string tool = (input.Tool ?? "").Trim();
                var issues = new List<string>();
                ToolCapabilityInfo? capability = null;

                if (tool.Length == 0)
                {
                    issues.Add("operation " + i + ": tool is required");
                }
                else
                {
                    capability = lookup == null ? null : lookup(tool);
                    if (capability == null)
                        issues.Add("operation " + i + ": unknown tool '" + tool + "'");
                }

                // 能力相关的规则只在工具真的存在时才判：未知工具已经有一条 issue，
                // 再猜它的 access/status 只会制造自相矛盾的输出。
                if (capability != null)
                {
                    if (capability.Status == ToolStatus.Planned)
                        issues.Add("operation " + i + ": '" + tool + "' is not supported on this TIA version (planned)");
                    if (capability.DirectOnly && !allowDirectOnly)
                        issues.Add("operation " + i + ": '" + tool + "' must be called by its own name; set allowDirectOnly=true to batch it");
                    if (capability.Access != ToolAccess.Read && !confirm)
                        issues.Add("operation " + i + ": '" + tool + "' writes; confirm=true is required");
                }

                string argumentsJson = input.ArgumentsJson ?? "";

                // 嵌套编排必须先在这里拒绝：RunBatch 的 BatchGate 是不可重入的 SemaphoreSlim，
                // 从批次里再调 RunBatch 会在同一把锁上等自己，永远不返回。CallTool 同理 ——
                // 它只是同一执行体的另一个入口，嵌套没有任何意义。
                // 名字比较不区分大小写：BuildPlan 的规范化发生在本函数之后，小写名字同样危险。
                if (string.Equals(tool, "RunBatch", StringComparison.OrdinalIgnoreCase))
                    issues.Add("operation " + i + ": RunBatch cannot be nested inside a batch");
                else if (string.Equals(tool, "CallTool", StringComparison.OrdinalIgnoreCase))
                    issues.Add("operation " + i + ": CallTool cannot be nested; name the target tool directly");
                else if ((string.Equals(tool, "InvokeObject", StringComparison.OrdinalIgnoreCase)
                          || string.Equals(tool, "InvokeService", StringComparison.OrdinalIgnoreCase))
                         && IsAllowWriteTrue(argumentsJson))
                    // 与 CallTool 的拒绝保持一致：allowWrite=true 能调到任意 public Openness 方法，
                    // 只有按本名调用，宿主的批准提示才能显示真正会执行什么。
                    issues.Add("operation " + i + ": '" + tool + "' with allowWrite=true cannot be nested; call it by its own name so the host names it");

                // 参数是不是合法的 JSON object 与工具无关，独立判断：写成数组或裸值
                // 同样是调用方的错误，应该在预检阶段一次说清。
                if (!string.IsNullOrWhiteSpace(argumentsJson) && !IsJsonObject(argumentsJson))
                    issues.Add("operation " + i + ": argumentsJson must be a JSON object");

                plan.Operations.Add(new BatchOperationPlan
                {
                    Index = i,
                    Tool = tool,
                    Access = capability == null ? default(ToolAccess) : capability.Access,
                    Status = capability == null ? default(ToolStatus) : capability.Status,
                    Destructive = capability != null && capability.Destructive,
                    DirectOnly = capability != null && capability.DirectOnly,
                    Known = capability != null,
                    WillRun = issues.Count == 0,
                    Issues = issues.ToArray(),
                });
            }

            plan.CanRun = plan.PlanIssues.Length == 0 && AllWillRun(plan.Operations);
            return plan;
        }

        /// <summary>Clamp to 1..200. Public so the adapter and the offline suite agree on the
        /// one interpretation of maxOperations (0 is not "no limit").</summary>
        public static int ClampMaxOperations(int maxOperations)
        {
            if (maxOperations < MinMaxOperations) return MinMaxOperations;
            if (maxOperations > DefaultMaxOperations) return DefaultMaxOperations;
            return maxOperations;
        }

        private static bool AllWillRun(List<BatchOperationPlan> operations)
        {
            for (int i = 0; i < operations.Count; i++)
                if (!operations[i].WillRun) return false;
            return true;
        }

        /// <summary>Reads the literal allowWrite flag the same way CallTool does: a JSON true or
        /// the string "true" (some hosts stringify booleans). Invalid JSON returns false here and
        /// is reported separately as "argumentsJson must be a JSON object".</summary>
        private static bool IsAllowWriteTrue(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return false;
            try
            {
                using (var document = JsonDocument.Parse(argumentsJson))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (!string.Equals(property.Name, "allowWrite", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (property.Value.ValueKind == JsonValueKind.True) return true;
                        if (property.Value.ValueKind == JsonValueKind.String
                            && string.Equals(property.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch (JsonException)
            {
                // 非法 JSON 由 IsJsonObject 统一报错，这里不重复制造第二条 issue。
            }
            return false;
        }

        /// <summary>Empty/whitespace arguments mean "no arguments" (CallTool treats them the same
        /// way). Anything else must parse to a JSON object, not an array or a bare value.</summary>
        private static bool IsJsonObject(string text)
        {
            try
            {
                using (var document = JsonDocument.Parse(text))
                    return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}

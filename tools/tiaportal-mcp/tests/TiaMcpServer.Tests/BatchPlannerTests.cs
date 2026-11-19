using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 批次预检的零依赖规则。判错了不会崩：一次本该跑的批次被拒，或者一次不该跑的
    /// 写入混进 RunBatch —— 后者会在真实工程上留下一半改过的状态。只有会失败的用例盯得住。
    /// </summary>
    internal static class BatchPlannerTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var capabilities = new Dictionary<string, ToolCapabilityInfo>(StringComparer.Ordinal)
            {
                ["GetBlocks"] = Cap("GetBlocks", ToolAccess.Read, ToolStatus.Verified),
                ["SetPlcTag"] = Cap("SetPlcTag", ToolAccess.Write, ToolStatus.Verified),
                ["DeletePlcBlock"] = Cap("DeletePlcBlock", ToolAccess.Delete, ToolStatus.Verified,
                    destructive: true, directOnly: true),
                ["DownloadToPlc"] = Cap("DownloadToPlc", ToolAccess.Write, ToolStatus.Verified,
                    destructive: true, directOnly: true),
                ["ExportAsDocuments"] = Cap("ExportAsDocuments", ToolAccess.Write, ToolStatus.Planned),
                // 编排工具本身也是真实工具：嵌套必须在预检阶段拒绝（BatchGate 不可重入）。
                ["RunBatch"] = Cap("RunBatch", ToolAccess.Write, ToolStatus.Implemented,
                    destructive: true, directOnly: true),
                ["CallTool"] = Cap("CallTool", ToolAccess.Write, ToolStatus.Implemented,
                    destructive: true),
                ["InvokeObject"] = Cap("InvokeObject", ToolAccess.Write, ToolStatus.Implemented,
                    destructive: true),
                ["InvokeService"] = Cap("InvokeService", ToolAccess.Write, ToolStatus.Implemented,
                    destructive: true),
            };

            ToolCapabilityInfo? Lookup(string name)
                => capabilities.TryGetValue(name, out var capability) ? capability : null;

            // ---- 空批：null 和空列表都要明确拒绝 ----
            var nullPlan = BatchPlanner.Plan(null, Lookup, confirm: true, allowDirectOnly: true);
            check(!nullPlan.CanRun && nullPlan.PlanIssues.Contains("batch is empty"),
                "a null batch is rejected as empty");
            var emptyPlan = BatchPlanner.Plan(new List<BatchOperationInput>(), Lookup, confirm: true, allowDirectOnly: true);
            check(!emptyPlan.CanRun && emptyPlan.PlanIssues.Contains("batch is empty"),
                "an empty batch is rejected as empty");

            // ---- 未知工具：点名操作序号和名字 ----
            var unknown = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("NoSuchTool", "{}") }, Lookup, true, true);
            check(!unknown.CanRun && !unknown.Operations[0].WillRun
                  && unknown.Operations[0].Issues.Any(i => i.Contains("unknown tool 'NoSuchTool'")),
                "an unknown tool is rejected with its name and operation index");
            // access/status 的枚举默认值刚好是 Read/Verified，不能让它看起来像「已验证的读」。
            check(!unknown.Operations[0].Known,
                "an unknown tool is marked Known=false so defaults are not trusted");
            check(unknown.Operations[0].Access == ToolAccess.Read
                  && unknown.Operations[0].Status == ToolStatus.Verified,
                "Known=false keeps the enum defaults documented (Read/Verified) rather than guessing");
            var knownOp = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", "") }, Lookup, true, true);
            check(knownOp.Operations[0].Known,
                "a resolved tool is marked Known=true");

            // ---- 版本门控：Planned 工具不属于这一版 ----
            var planned = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("ExportAsDocuments", "{}") }, Lookup, true, true);
            check(!planned.CanRun
                  && planned.Operations[0].Issues.Any(i => i.Contains("not supported on this TIA version (planned)")),
                "a Planned tool is rejected before anything runs");

            // ---- DirectOnly：默认拒绝，显式 opt-in 后才放行 ----
            var direct = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("DeletePlcBlock", "{}") }, Lookup, confirm: true, allowDirectOnly: false);
            check(!direct.CanRun && !direct.Operations[0].WillRun
                  && direct.Operations[0].Issues.Any(i => i.Contains("must be called by its own name")),
                "a direct-only tool is rejected without allowDirectOnly");

            var directAllowed = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("DeletePlcBlock", "{}") }, Lookup, confirm: true, allowDirectOnly: true);
            check(directAllowed.CanRun && directAllowed.Operations[0].WillRun
                  && directAllowed.Operations[0].DirectOnly,
                "allowDirectOnly=true permits a direct-only tool after the other checks pass");

            // ---- 嵌套编排：RunBatch 的 BatchGate 不可重入，必须在预检阶段拒绝 ----
            // PlanBatch / RunBatch 的 BuildPlan 直接调用 BatchPlanner.Plan，所以这里钉住的
            // 就是两者共用的规则；一旦放行，RunBatch 会在同一把信号量上等自己直到取消。
            var nestedRun = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("RunBatch", "{}") }, Lookup, confirm: true, allowDirectOnly: true);
            check(!nestedRun.CanRun && !nestedRun.Operations[0].WillRun
                  && nestedRun.Operations[0].Issues.Any(i => i.Contains("RunBatch cannot be nested inside a batch")),
                "RunBatch cannot be nested inside a batch");

            // 名字大小写不能绕过：BuildPlan 的规范化发生在 Plan 之后。
            var nestedRunLower = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("runbatch", "{}") }, Lookup, confirm: true, allowDirectOnly: true);
            check(!nestedRunLower.CanRun
                  && nestedRunLower.Operations[0].Issues.Any(i => i.Contains("RunBatch cannot be nested")),
                "nested RunBatch is rejected case-insensitively");

            var nestedCall = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("CallTool", "{}") }, Lookup, confirm: true, allowDirectOnly: true);
            check(!nestedCall.CanRun && !nestedCall.Operations[0].WillRun
                  && nestedCall.Operations[0].Issues.Any(i =>
                      i.Contains("CallTool cannot be nested") && i.Contains("name the target tool directly")),
                "CallTool cannot be nested; the target tool must be named directly");

            foreach (var bridge in new[] { "InvokeObject", "InvokeService" })
            {
                var writeBridge = BatchPlanner.Plan(
                    new List<BatchOperationInput> { Op(bridge, "{\"allowWrite\":true}") },
                    Lookup, confirm: true, allowDirectOnly: true);
                check(!writeBridge.CanRun && !writeBridge.Operations[0].WillRun
                      && writeBridge.Operations[0].Issues.Any(i =>
                          i.Contains("allowWrite=true cannot be nested") && i.Contains("by its own name")),
                    bridge + " with allowWrite=true cannot be nested");

                // 反向哨兵：没开 allowWrite 的反射桥只是普通只读式调用，不应当被这一条误杀。
                var readBridge = BatchPlanner.Plan(
                    new List<BatchOperationInput> { Op(bridge, "{\"allowWrite\":false}") },
                    Lookup, confirm: true, allowDirectOnly: true);
                check(readBridge.CanRun && readBridge.Operations[0].WillRun,
                    "[sentinel] " + bridge + " with allowWrite=false may still be batched");
            }

            // ---- 写工具必须 confirm ----
            var write = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("SetPlcTag", "{}") }, Lookup, confirm: false, allowDirectOnly: false);
            check(!write.CanRun
                  && write.Operations[0].Issues.Any(i => i.Contains("writes; confirm=true is required")),
                "a write without confirm=true is rejected");

            // 反向哨兵：只读工具不需要 confirm，否则「要确认」就失去了区分度。
            var read = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", "") }, Lookup, confirm: false, allowDirectOnly: false);
            check(read.CanRun && read.Operations[0].WillRun,
                "[sentinel] a read-only operation runs without confirm");

            // ---- 参数 JSON：必须是 object，不能是数组、裸值或坏 JSON ----
            foreach (var bad in new[] { "[]", "[1,2]", "42", "\"text\"", "{\"a\":", "not json" })
            {
                var plan = BatchPlanner.Plan(
                    new List<BatchOperationInput> { Op("GetBlocks", bad) }, Lookup, true, true);
                check(!plan.CanRun
                      && plan.Operations[0].Issues.Any(i => i.Contains("argumentsJson must be a JSON object")),
                    "argumentsJson '" + bad + "' is rejected as not a JSON object");
            }
            var whitespaceArgs = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", "  ") }, Lookup, true, true);
            check(whitespaceArgs.CanRun, "blank argumentsJson means 'no arguments'");

            // ---- 上限：200，超过即拒绝；自定义上限同样生效 ----
            var tooMany = BatchPlanner.Plan(
                Enumerable.Range(0, BatchPlanner.DefaultMaxOperations + 1)
                    .Select(_ => Op("GetBlocks", ""))
                    .ToList(),
                Lookup, true, true);
            check(!tooMany.CanRun
                  && tooMany.PlanIssues.Any(i => i.Contains("the limit is 200")),
                "more than 200 operations is rejected with the limit");

            var customLimit = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", ""), Op("GetBlocks", "") },
                Lookup, true, true, maxOperations: 1);
            check(!customLimit.CanRun && customLimit.MaxOperations == 1
                  && customLimit.PlanIssues.Any(i => i.Contains("the limit is 1")),
                "a custom maxOperations limit is enforced and reported");

            // maxOperations 夹在 1..200：0 不是「不限量」。
            var zeroLimit = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", "") }, Lookup, true, true, maxOperations: 0);
            check(zeroLimit.MaxOperations == 1 && zeroLimit.CanRun,
                "maxOperations=0 clamps to 1, not to unlimited");
            var negativeLimit = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", "") }, Lookup, true, true, maxOperations: -1);
            check(negativeLimit.MaxOperations == 1 && negativeLimit.CanRun,
                "maxOperations=-1 clamps to 1, not to unlimited");
            var hugeLimit = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", "") }, Lookup, true, true, maxOperations: 10000);
            check(hugeLimit.MaxOperations == BatchPlanner.DefaultMaxOperations,
                "maxOperations above 200 clamps to 200");

            // ---- 合法批：一个读 + 一个写（已 confirm），两条都 WillRun ----
            var valid = BatchPlanner.Plan(
                new List<BatchOperationInput>
                {
                    Op("GetBlocks", "{\"softwarePath\":\"PLC_1\"}"),
                    Op("SetPlcTag", "{\"tagName\":\"Tag_1\",\"value\":1}"),
                },
                Lookup, confirm: true, allowDirectOnly: false);
            check(valid.CanRun && valid.Operations.Count == 2
                  && valid.Operations.All(o => o.WillRun && o.Issues.Length == 0),
                "a valid read+write batch (confirm=true) can run");
            check(valid.Operations[0].Index == 0 && valid.Operations[1].Index == 1,
                "operations keep their submitted order and indexes");

            // ---- CanRun 是 AND：任何一条有 issue，整批都不能跑 ----
            var mixed = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("GetBlocks", ""), Op("NoSuchTool", "") },
                Lookup, true, true);
            check(!mixed.CanRun && mixed.Operations[0].WillRun && !mixed.Operations[1].WillRun,
                "one bad operation makes the whole batch un-runnable");

            // 空 Tool 文本单独一条 issue，不崩也不查找。
            var missingTool = BatchPlanner.Plan(
                new List<BatchOperationInput> { Op("  ", "") }, Lookup, true, true);
            check(!missingTool.CanRun
                  && missingTool.Operations[0].Issues.Any(i => i.Contains("tool is required")),
                "an empty tool name is rejected as required");
        }

        private static BatchOperationInput Op(string tool, string argumentsJson)
            => new BatchOperationInput { Tool = tool, ArgumentsJson = argumentsJson };

        private static ToolCapabilityInfo Cap(
            string name, ToolAccess access, ToolStatus status,
            bool destructive = false, bool directOnly = false)
            => new ToolCapabilityInfo
            {
                Name = name,
                Access = access,
                Status = status,
                Destructive = destructive,
                DirectOnly = directOnly,
            };
    }
}

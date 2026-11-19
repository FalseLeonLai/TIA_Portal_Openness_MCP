using System;
using System.Collections.Generic;
using System.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 能力矩阵：每个工具在调用前就要能说清「读还是写、验没验过、需要什么前置」。
    /// 判错了不会崩 —— 模型只会拿着一份错误的预期去调用工具（比如以为 V19 能用
    /// SIMATIC SD 导出，或者以为写工具不需要确认）—— 只有会失败的用例盯得住。
    /// </summary>
    internal static class ToolCapabilityTests
    {
        internal static void Run(Action<bool, string> check, Action<string, string> skip)
        {
            // ---- Category：取第二个方括号，Category: 前缀要去掉，没有标签就归 General ----
            check(ToolCapabilities.Classify("AnyTool", "[L0][Meta] search tools").Category == "Meta",
                "category is taken from the second bracket ([L0][Meta])");
            check(ToolCapabilities.Classify("AnyTool", "[L2][PLC-Software] export").Category == "PLC-Software",
                "category without the Category: prefix is kept as-is");
            check(ToolCapabilities.Classify("AnyTool", "[L2][Category:PLC-Online] online reads").Category == "PLC-Online",
                "Category: prefix is stripped ([L2][Category:PLC-Online] -> PLC-Online)");
            check(ToolCapabilities.Classify("AnyTool", "No layer tag here").Category == "General",
                "descriptions without a layer tag fall back to General");

            // ---- Access：ToolSafety 只读 -> Read；Delete 前缀 -> Delete；其余 -> Write ----
            check(ToolCapabilities.Classify("GetBlocks", "[L1][PLC-Software] read").Access == ToolAccess.Read,
                "GetBlocks is Read");
            check(ToolCapabilities.Classify("SetPlcTag", "[L1][PLC-Software] write").Access == ToolAccess.Write,
                "SetPlcTag is Write");
            check(ToolCapabilities.Classify("DeletePlcBlock", "[L2][PLC-Software] delete").Access == ToolAccess.Delete,
                "DeletePlcBlock is Delete");

            // Delete 前缀必须真的覆盖 Write，否则「删除」会被降级成一次普通写入。
            check(ToolCapabilities.Classify("DeletePlcTagTable", "").Access == ToolAccess.Delete,
                "a Delete* tool is reported as Delete, never as a plain Write");

            // ---- Status：Verified 必须带 per-tool 证据，不能共用一句空话 ----
            var getBlocks = ToolCapabilities.Classify("GetBlocks", "[L1][PLC-Software] read blocks", 19);
            check(getBlocks.Status == ToolStatus.Verified,
                "GetBlocks is Verified on V19");
            check(getBlocks.Evidence == "README.V19 真机矩阵 2026-10-30",
                "GetBlocks cites the 2026-10-30 V19 real-machine matrix");

            var setTag = ToolCapabilities.Classify("SetPlcTag", "[L1][PLC-Software] write a tag", 19);
            check(setTag.Status == ToolStatus.Verified
                  && setTag.Evidence.Contains("改名除外"),
                "SetPlcTag carries the rename exception in its evidence");

            var download = ToolCapabilities.Classify("DownloadToPlc", "[L2][PLC-Online] download", 21);
            check(download.Status == ToolStatus.Verified
                  && download.Evidence == "CHANGELOG 2.2.5 / 2.2.7 V21 真机",
                "DownloadToPlc cites the CHANGELOG 2.2.5/2.2.7 V21 real-machine run");
            check(ToolCapabilities.Classify("ConnectIsolated", "", 21).Status == ToolStatus.Verified,
                "ConnectIsolated is Verified (CHANGELOG 2.2.5/2.2.7)");

            // 不是这四组证据里的工具，一律不许自称 Verified —— 包括历史上真机用过、
            // 但没有一条可引用的 per-tool 记录的连接/打开工程类工具。
            foreach (var name in new[] { "Connect", "OpenProject", "ImportBlock", "CompileAndDiagnosePlc", "GoOnline" })
            {
                check(ToolCapabilities.Classify(name, "", 19).Status != ToolStatus.Verified,
                    name + " is not Verified without per-tool evidence");
            }

            // README.V19.md 2026-11-18 明确说这两个没上真机；不能被塞进 Verified。
            foreach (var name in new[] { "GetBlockInterface", "GetTypeStructure" })
            {
                check(ToolCapabilities.Classify(name, "[L2][PLC-Software] read", 19).Status == ToolStatus.Unverified,
                    name + " is Unverified on V19 (README says it was not real-machine tested)");
                check(ToolCapabilities.Classify(name, "[L2][PLC-Software] read", 21).Status == ToolStatus.Unverified,
                    name + " stays Unverified on V21");
                check(ToolCapabilities.UnverifiedToolNames.Contains(name),
                    name + " is in the explicit Unverified name set");
            }

            check(ToolCapabilities.Classify("GetHmiScreens", "[L2][HMI] read screens", 19).Status == ToolStatus.Unverified,
                "HMI tools are Unverified (the test project has no HMI device)");

            // ---- SIMATIC SD：V19 Planned；V20+ 只有 2.2.9 实测过的两个才 Verified ----
            var sdV19 = ToolCapabilities.Classify(
                "ExportAsDocuments", "[L2][PLC-Software] PREFERRED on V21+", 19);
            check(sdV19.Status == ToolStatus.Planned,
                "ExportAsDocuments is Planned on V19");
            check(sdV19.Evidence == "SIMATIC SD requires V20+",
                "Planned SIMATIC SD carries the V20+ evidence");

            check(ToolCapabilities.Classify(
                    "ExportAsDocuments", "[L2][PLC-Software] PREFERRED on V21+", 21).Status == ToolStatus.Unverified,
                "ExportAsDocuments is Unverified on V21 (version gate passes, no live run recorded)");
            check(ToolCapabilities.Classify(
                    "ImportBlocksFromDocuments", "[L2][PLC-Software]", 21).Status == ToolStatus.Unverified,
                "ImportBlocksFromDocuments is Unverified on V21");

            foreach (var name in new[] { "ImportFromDocuments", "ExportBlocksAsDocuments" })
            {
                check(ToolCapabilities.Classify(name, "[L2][PLC-Software]", 19).Status == ToolStatus.Planned,
                    name + " is Planned on V19");
                var live = ToolCapabilities.Classify(name, "[L2][PLC-Software]", 21);
                check(live.Status == ToolStatus.Verified
                      && live.Evidence == "CHANGELOG 2.2.9 V20/V21 活工程实测",
                    name + " is Verified on V21 with the CHANGELOG 2.2.9 evidence");
                check(ToolCapabilities.Classify(name, "[L2][PLC-Software]", 20).Status == ToolStatus.Verified,
                    name + " is Verified on V20 (the V20+ gate passes)");
                check(ToolCapabilities.Classify(name, "[L2][PLC-Software]", 0).Status == ToolStatus.Unverified,
                    name + " is Unverified when the TIA version is unknown");
            }

            // 版本未知（0）时不能断言「当前版本不支持」：只能说还没验证。
            check(ToolCapabilities.Classify(
                    "ExportAsDocuments", "[L2][PLC-Software]", 0).Status == ToolStatus.Unverified,
                "unknown TIA version never reports Planned");

            // ---- VCI：真实工具名，V21 门不足 Planned，门通过后仍是 Unverified ----
            foreach (var name in new[]
                     {
                         "GetVersionControlWorkspaces", "CreateVersionControlWorkspace",
                         "GetVersionControlStatus", "SyncVersionControlWorkspace", "ConnectProjectToWorkspace",
                     })
            {
                var v20 = ToolCapabilities.Classify(name, "[L2][Category:VersionControl]", 20);
                check(v20.Status == ToolStatus.Planned && v20.Requires.Contains("V21+"),
                    name + " is Planned and requires V21+ on V20");
                var v21 = ToolCapabilities.Classify(name, "[L2][Category:VersionControl]", 21);
                check(v21.Status == ToolStatus.Unverified && v21.Requires.Contains("V21+"),
                    name + " is Unverified (not Verified) on V21 and still requires V21+");
            }

            // Verified / Unverified 两个显式集合必须不相交；哨兵用例不再拿两个不同工具互比。
            var overlap = ToolCapabilities.VerifiedToolNames
                .Intersect(ToolCapabilities.UnverifiedToolNames, StringComparer.Ordinal)
                .ToList();
            check(overlap.Count == 0,
                "the Verified and Unverified name sets are disjoint (overlap: " + string.Join(",", overlap) + ")");

            // ---- Requires：固定顺序且去重 ----
            check(download.Requires.Contains("portal")
                  && download.Requires.Contains("online")
                  && download.Requires.Contains("confirm"),
                "DownloadToPlc requires portal/online/confirm");
            check(Array.IndexOf(download.Requires, "portal") < Array.IndexOf(download.Requires, "online")
                  && Array.IndexOf(download.Requires, "online") < Array.IndexOf(download.Requires, "confirm"),
                "Requires keeps the fixed portal -> online -> confirm order");

            // dryRun 来自显式的 DryRunTools 集合，而不是描述里出现这个词。
            check(ToolCapabilities.Classify("SetDeviceItemIoAddress", "", 19).Requires.Contains("dryRun"),
                "SetDeviceItemIoAddress requires dryRun (it declares bool dryRun)");
            check(ToolCapabilities.Classify("DeletePlcBlock", "", 19).Requires.Contains("dryRun"),
                "DeletePlcBlock requires dryRun");
            check(ToolCapabilities.Classify("ScaffoldProject", "", 19).Requires.Contains("dryRun"),
                "ScaffoldProject requires dryRun");
            check(!ToolCapabilities.Classify("DeletePlcExternalSource", "", 19).Requires.Contains("dryRun"),
                "DeletePlcExternalSource has no dryRun parameter, so it must not advertise dryRun");
            check(!ToolCapabilities.Classify("ImportBlocksFromDirectory", "", 19).Requires.Contains("dryRun"),
                "ImportBlocksFromDirectory has no dryRun parameter, so it must not advertise dryRun");
            check(ToolCapabilities.Classify("ImportPlcProgramFromDirectory", "", 19).Requires.Contains("dryRun"),
                "ImportPlcProgramFromDirectory declares dryRun, so it advertises it");

            var capabilityTool = ToolCapabilities.Classify(
                "GetToolCapabilities",
                "[L0][Meta] ... prerequisites (portal, online, confirm, dryRun, V20+, V21+).", 19);
            check(!capabilityTool.Requires.Contains("dryRun"),
                "GetToolCapabilities does not require dryRun just because its description names it");

            var ioAddress = ToolCapabilities.Classify(
                "SetDeviceItemIoAddress",
                "[L2][Hardware][Destructive] Defaults to dryRun=true. Preview or change the address.",
                19);
            check(ioAddress.Requires.Contains("dryRun"),
                "SetDeviceItemIoAddress requires dryRun");
            check(ioAddress.Requires.Distinct().Count() == ioAddress.Requires.Length,
                "Requires contains no duplicates");

            // 用真正 portal-free 的工具做反向哨兵：未知名字会保守地要求 portal，
            // 那正是 ToolSafety 的默认值，不能拿来证明「空数组」。
            check(ToolCapabilities.Classify("GetExport", "[L0][Meta] read the export store", 19).Requires.Length == 0,
                "a portal-free tool with no prerequisites has an empty Requires array");

            check(ToolCapabilities.Classify("ExportAsDocuments", "[L2][PLC-Software]", 21)
                    .Requires.Contains("V20+"),
                "version-gated tools advertise V20+ regardless of the current engine");

            // ---- 直接取自 ToolSafety 的标记 ----
            check(ToolCapabilities.Classify("DeletePlcBlock", "").DirectOnly,
                "DeletePlcBlock keeps the DirectOnly flag");
            check(ToolCapabilities.Classify("DownloadToPlc", "").Destructive
                  && ToolCapabilities.Classify("DownloadToPlc", "").OpenWorld,
                "DownloadToPlc keeps Destructive + OpenWorld");

            // ---- All：保持调用方顺序，逐项分类 ----
            var all = ToolCapabilities.All(new (string Name, string Description)[]
            {
                ("SetPlcTag", "[L1][PLC-Software] write a tag"),
                ("GetBlocks", "[L1][PLC-Software] read blocks"),
            }, 19);
            check(all.Count == 2 && all[0].Name == "SetPlcTag" && all[1].Name == "GetBlocks",
                "All preserves the caller's order and classifies every entry");
            check(ToolCapabilities.All(null!, 19).Count == 0,
                "All tolerates a null roster");

            // ---- 新工具的安全标记：能力查询本身不许占 Openness 句柄 ----
            var capabilitySafety = ToolSafety.Classify("GetToolCapabilities");
            check(capabilitySafety.ReadOnly && !capabilitySafety.TouchesPortal,
                "GetToolCapabilities is read-only and portal-free");
            // 反向哨兵：绝大多数读工具仍然需要 Openness 句柄，否则「portal-free」就失去了区分度。
            check(ToolSafety.Classify("GetBlocks").ReadOnly && ToolSafety.Classify("GetBlocks").TouchesPortal,
                "[sentinel] GetBlocks is read-only but still needs the portal");

            RunQueryTests(check);
        }

        /// <summary>过滤/夹取是纯逻辑，坏了只会让模型拿错清单或一次拿到全表 —— 必须离线钉住。</summary>
        private static void RunQueryTests(Action<bool, string> check)
        {
            var roster = ToolCapabilities.All(new (string Name, string Description)[]
            {
                ("ExportPlcWatchTable", "[L2][Category:PLC-Online] export a watch table to a file"),
                ("GetBlocks", "[L1][PLC-Software] read blocks"),
                ("Mystery", "[L2][Category:PLC-Online] something unrelated"),
            }, 19);

            // 多词查询：所有词都必须命中（name/description/category 任一即可）。
            var multi = ToolCapabilityQuery.Filter(roster, "watch table", "", "", 50, out int multiTotal, out var multiError);
            check(multiError == null && multiTotal == 1 && multi.Count == 1 && multi[0].Name == "ExportPlcWatchTable",
                "multi-term query requires every word to match and returns the right tool");

            // 类别命中：名字/描述都没有 plc-online，只有 Category 有。
            var byCategory = ToolCapabilityQuery.Filter(roster, "plc-online", "", "", 50, out int categoryTotal, out var categoryError);
            check(categoryError == null && categoryTotal == 2
                  && byCategory.Any(c => c.Name == "Mystery"),
                "query matches the category, not only the name/description");

            // 非法过滤器：返回错误 + 空页，而不是静默忽略。
            var badStatus = ToolCapabilityQuery.Filter(roster, "", "bogus", "", 50, out int badTotal, out var badStatusError);
            check(badStatusError != null && badStatusError.Contains("Unknown status")
                  && badTotal == 0 && badStatus.Count == 0,
                "invalid status is rejected with a helpful error and an empty page");
            var badAccess = ToolCapabilityQuery.Filter(roster, "", "", "deletee", 50, out int badAccessTotal, out var badAccessError);
            check(badAccessError != null && badAccessError.Contains("Unknown access")
                  && badAccessTotal == 0 && badAccess.Count == 0,
                "invalid access is rejected with a helpful error and an empty page");

            // 合法过滤器（大小写不敏感）。
            var reads = ToolCapabilityQuery.Filter(roster, "", "verified", "READ", 50, out int readTotal, out var readError);
            check(readError == null && readTotal == 1 && reads.Count == 1 && reads[0].Name == "GetBlocks",
                "status/access filters are case-insensitive and compose");

            // limit 语义：<=0 -> 50，>500 -> 500。用足够大的 roster 钉住两端。
            var many = new List<(string Name, string Description)>();
            for (int i = 0; i < 60; i++)
                many.Add(("GetTool" + i.ToString("000"), "[L1][PLC-Software] synthetic tool"));
            var sixty = ToolCapabilities.All(many, 19);
            var zero = ToolCapabilityQuery.Filter(sixty, "", "", "", 0, out int zeroTotal, out var zeroError);
            check(zeroError == null && zeroTotal == 60 && zero.Count == ToolCapabilityQuery.DefaultLimit,
                "limit=0 (or negative) falls back to the default 50, while Total stays 60");

            var huge = new List<(string Name, string Description)>();
            for (int i = 0; i < 510; i++)
                huge.Add(("GetTool" + i.ToString("000"), "[L1][PLC-Software] synthetic tool"));
            var fiveTen = ToolCapabilities.All(huge, 19);
            var capped = ToolCapabilityQuery.Filter(fiveTen, "", "", "", 501, out int cappedTotal, out var cappedError);
            check(cappedError == null && cappedTotal == 510 && capped.Count == ToolCapabilityQuery.MaxLimit,
                "limit above 500 is clamped to 500 while Total reports all 510 matches");

            // Total vs 页大小：Total 是截断前命中数，不是本页条数。
            var page = ToolCapabilityQuery.Filter(roster, "", "", "", 1, out int pageTotal, out var pageError);
            check(pageError == null && pageTotal == 3 && page.Count == 1,
                "Total counts matches before the limit; Items is only the current page");
        }
    }
}

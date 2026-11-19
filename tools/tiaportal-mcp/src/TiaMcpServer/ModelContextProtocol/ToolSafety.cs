using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>What a host may assume about one tool. Mirrors the MCP ToolAnnotations hints,
    /// plus the two decisions this server makes on its own: whether the call needs the shared
    /// TIA Portal handle, and whether it may be reached through CallTool at all.</summary>
    public sealed class ToolSafetyInfo
    {
        public bool ReadOnly { get; set; }
        public bool Destructive { get; set; }
        public bool Idempotent { get; set; }
        /// <summary>Talks to something outside the engineering station: a physical CPU over
        /// the network, PLCSIM, an OPC UA server.</summary>
        public bool OpenWorld { get; set; }
        /// <summary>Uses the process-wide Openness handle and so must not overlap another call.</summary>
        public bool TouchesPortal { get; set; }
        /// <summary>Must be called by its own name so the host's approval prompt names it.
        /// CallTool refuses these.</summary>
        public bool DirectOnly { get; set; }
    }

    /// <summary>
    /// The single table behind tool annotations, the Openness serialization gate, the CallTool
    /// refusal list and the safety self-test. Zero dependencies so the offline suite can pin it.
    ///
    /// Everything not matched by a rule below is treated as a mutating, non-destructive,
    /// non-idempotent, closed-world call that needs the portal: the conservative default.
    /// </summary>
    public static class ToolSafety
    {
        /// <summary>Tools that change a running CPU or delete engineering data. In the default lite
        /// profile they are listed directly, and CallTool refuses them, so a host that asks before
        /// each tool call shows the user "DownloadToPlc" rather than an opaque "CallTool".</summary>
        private static readonly HashSet<string> DirectOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "DownloadToPlc", "GoOnline", "SetWatchTableModifyValue",
            "DeletePlcBlock", "DeletePlcType", "DeletePlcTagTable", "DeletePlcExternalSource",
            "DeletePlcTag", "DeletePlcWatchTable", "DeleteDevice",
            // 整批调用会放大一条写入/删除的影响面；让宿主在批准提示里看到的是 RunBatch
            // 而不是一个语焉不详的 CallTool。
            "RunBatch",
        };

        public static IReadOnlyCollection<string> DirectOnlyTools => DirectOnly;

        // Proven not to touch the TIA Portal handle: the tool search and the in-memory export store.
        private static readonly HashSet<string> PortalFree = new HashSet<string>(StringComparer.Ordinal)
        {
            // 能力矩阵只读反射目录，和 FindTools 一样不碰 TIA 句柄。
            // 批次预检与账本读取同理：PlanBatch 只做静态检查，GetOpsLedger 只读本地文件。
            "FindTools", "GetToolCapabilities", "GetAuthoringGuide",
            "PlanBatch", "GetOpsLedger",
            "GetExport", "ListExports", "SaveExport", "DeleteExport", "ClearExports",
        };

        // Verbs whose tools only read (the project, files, or a CPU) and return data.
        private static readonly string[] ReadOnlyPrefixes =
        {
            "Get", "Describe", "List", "Find", "Search", "Analyze", "Probe", "Check", "Compare",
            "Trace", "Read", "Sample", "Monitor", "Dump", "Plan", "Validate", "Build", "Compose",
        };

        private static readonly HashSet<string> ReadOnlyExtra = new HashSet<string>(StringComparer.Ordinal)
        {
            // GetToolCapabilities 已经会被 "Get" 前缀判成只读；显式列出是防止将来改名/加规则时
            // 悄悄把它降级成写工具（CallTool 就会因此多要一次确认）。
            "GetToolCapabilities",
            // Plan/Get 前缀已经会判成只读；显式写出防止将来改规则时把预检或账本读取
            // 悄悄降级成写工具（那样 CallTool 会多要一次确认，甚至拒绝转发）。
            "PlanBatch", "GetOpsLedger",
            "RunOnlineMonitoringSafetySelfTest", "RunHmiActionScriptRecipeSafetySelfTest",
        };

        // Prefix matches that are NOT read-only despite the verb.
        private static readonly HashSet<string> NotReadOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            // Writes the export into a caller-chosen file.
            "SaveExport",
        };

        // Verbs that overwrite or remove existing engineering data.
        private static readonly string[] DestructivePrefixes =
        {
            "Delete", "Import", "Move", "Set", "Apply", "Sync", "Repair", "Seed",
        };

        private static readonly HashSet<string> DestructiveExtra = new HashSet<string>(StringComparer.Ordinal)
        {
            // Can invoke anything, so it inherits the worst case.
            "CallTool", "InvokeObject", "InvokeService",
            // Close (or replace) the open project; unsaved edits are lost.
            "CloseProject", "OpenProject", "CreateProject", "ScaffoldProject", "Disconnect",
            // Drop live online sessions, including one the user opened in the TIA UI.
            "GoOffline", "GoOfflineAll",
            "DownloadToPlc", "PlcBuildAndImport", "GenerateBlocksFromExternalSource",
            "ClearExports",
            // A batch can contain any write or direct-only tool; inherit the worst case.
            "RunBatch",
        };

        private static readonly HashSet<string> OpenWorldTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "DownloadToPlc", "GoOnline", "GoOffline", "GoOfflineAll", "GetOnlineState",
            "CompareSoftwareToOnline", "GetPlcRunStateS7", "ProbeS7CpuIdentity",
            "ReadPlcLiveValuesS7", "ReadPlcLiveValuesOpcUa", "SamplePlcLiveValuesS7",
            "MonitorWatchTableLiveS7", "TraceTagCauseLive",
            // 批次里可能包含下载/在线工具（allowDirectOnly=true），整批的可达面就是其中
            // 最开放的那一条。保守地标 online，让宿主在预检后按「可能连 CPU」提示用户。
            "RunBatch",
        };

        // Repeating the call with the same arguments leaves the same end state.
        private static readonly string[] IdempotentPrefixes =
        {
            "Ensure", "Bind", "Set", "Export", "Save", "Compile", "Connect", "Attach", "GoOffline",
        };

        // Prefix matches that are NOT idempotent despite the verb.
        private static readonly HashSet<string> NotIdempotent = new HashSet<string>(StringComparer.Ordinal)
        {
            // Starts one more headless TIA Portal instance on every call.
            "ConnectIsolated",
        };

        public static ToolSafetyInfo Classify(string? toolName)
        {
            string name = toolName ?? "";
            bool readOnly = !NotReadOnly.Contains(name)
                && (ReadOnlyExtra.Contains(name) || StartsWithAny(name, ReadOnlyPrefixes));
            bool destructive = !readOnly
                && (DestructiveExtra.Contains(name) || StartsWithAny(name, DestructivePrefixes));
            bool idempotent = readOnly
                || (!NotIdempotent.Contains(name) && StartsWithAny(name, IdempotentPrefixes));

            return new ToolSafetyInfo
            {
                ReadOnly = readOnly,
                Destructive = destructive,
                Idempotent = idempotent,
                OpenWorld = OpenWorldTools.Contains(name),
                TouchesPortal = !PortalFree.Contains(name),
                DirectOnly = DirectOnly.Contains(name),
            };
        }

        /// <summary>A verb prefix only counts at a word boundary: "Settle" is not "Set".</summary>
        private static bool StartsWithAny(string name, string[] prefixes)
        {
            foreach (var p in prefixes)
            {
                if (name.Length > p.Length
                    && name.StartsWith(p, StringComparison.Ordinal)
                    && char.IsUpper(name[p.Length]))
                    return true;
                if (name.Length == p.Length && name == p) return true;
            }
            return false;
        }
    }
}

using System;
using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>What calling the tool does to the project: only reads, writes, or deletes.
    /// 比 <see cref="ToolSafetyInfo.ReadOnly"/> 多一档 Delete，因为「删掉了什么」和「改了点什么」
    /// 对调用方是两种完全不同的后果，宿主想分别展示。</summary>
    public enum ToolAccess { Read, Write, Delete }

    /// <summary>How much this repository actually knows about the tool.
    /// 保守分级：没在真机上跑过的工具宁可标 Unverified，也不要让调用方以为已经验证。</summary>
    public enum ToolStatus { Verified, Implemented, Unverified, Planned }

    /// <summary>
    /// One tool's capability record, returned by GetToolCapabilities and used to enrich FindTools.
    /// 全部是公开 get/set 属性 + 无参构造：MCP SDK 用 System.Text.Json 直接序列化它，
    /// 任何只读/计算属性都可能在不同 SDK 版本里被跳过，反而让调用方拿到缺字段的 JSON。
    /// </summary>
    public sealed class ToolCapabilityInfo
    {
        public string Name { get; set; } = "";
        public string Category { get; set; } = "General";
        public ToolAccess Access { get; set; }
        public ToolStatus Status { get; set; }
        public bool Destructive { get; set; }
        public bool DirectOnly { get; set; }
        public bool OpenWorld { get; set; }
        /// <summary>Fixed-order prerequisites (portal / online / confirm / dryRun / V20+ / V21+).
        /// 空数组表示没有额外前置。顺序固定是为了让文本输出可 diff、测试可断言。</summary>
        public string[] Requires { get; set; } = Array.Empty<string>();
        /// <summary>Why the status is what it is. Verified 必须能指回一次真机验证，而不是一句空话。</summary>
        public string Evidence { get; set; } = "";

        /// <summary>The full tool description, kept for the query helper so "watch table" can match a
        /// tool whose name says only ExportPlcWatchTableCsv. internal 是故意的：System.Text.Json 默认
        /// 只序列化 public 成员，所以它不会出现在 GetToolCapabilities 的 JSON 里，不改变返回契约。</summary>
        internal string Description { get; set; } = "";
    }

    /// <summary>
    /// Derives a <see cref="ToolCapabilityInfo"/> from the one safety table plus the [Lx][Category]
    /// description prefix. 零依赖（只引用同为零依赖的 ToolSafety），这样离线测试套件可以直接
    /// 链接本文件钉住判定结果 —— 判定错了不会崩，只会让模型在调用前拿到错误的预期。
    /// </summary>
    public static class ToolCapabilities
    {
        private const string SdDocumentsEvidence = "SIMATIC SD requires V20+";
        private const string VciEvidence = "Version control requires V21+";

        // Per-tool evidence, not one blanket string: a README that says "not verified" must not be
        // cited as proof. 每一条都能指回具体文档的某一句；没有证据的工具就不配 Verified。
        private static readonly Dictionary<string, string> VerifiedTools =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // README.V19.md 真机矩阵（2026-10-30）—— PLC 块 / 变量表 / 编译 / 导出 / 保存 /
            // 逻辑回读 / 软件单元。运行时只读监控不在该矩阵里，见下面两条独立证据。
            { "ScaffoldProject", "README.V19 真机矩阵 2026-10-30" },
            { "CompileSoftware", "README.V19 真机矩阵 2026-10-30" },
            { "CompileHardware", "README.V19 真机矩阵 2026-10-30" },
            { "SaveProject", "README.V19 真机矩阵 2026-10-30" },
            { "DescribeBlockLogic", "README.V19 真机矩阵 2026-10-30" },
            { "ExportBlock", "README.V19 真机矩阵 2026-10-30" },
            { "ExportBlocks", "README.V19 真机矩阵 2026-10-30" },
            { "GetBlocks", "README.V19 真机矩阵 2026-10-30" },
            { "GetBlockInfo", "README.V19 真机矩阵 2026-10-30" },
            { "GetPlcTags", "README.V19 真机矩阵 2026-10-30" },
            { "GetPlcTagTables", "README.V19 真机矩阵 2026-10-30" },
            { "CreatePlcTagTable", "README.V19 真机矩阵 2026-10-30" },
            { "DeletePlcBlock", "README.V19 真机矩阵 2026-10-30" },
            { "GetSoftwareUnits", "README.V19 真机矩阵 2026-10-30" },
            // 运行时读值工具不在 2026-10-30 的 Openness 真机矩阵里；它们的真机记录在
            // docs/在线实时读值_使用指南.md §八（CPU 1211C @192.168.0.32），逐工具引用。
            { "ReadPlcLiveValuesS7", "docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，S7 M/I/Q + 非优化 DB）" },
            { "ReadPlcLiveValuesOpcUa", "docs/在线实时读值_使用指南.md 真机实测（1211C @192.168.0.32，OPC UA 节点）" },
            // V19 真机验证过，但改名子功能是已知例外（README 已知问题 2）。
            { "SetPlcTag", "README.V19 真机矩阵 2026-10-30（改名除外，见已知问题 2）" },
            // CHANGELOG 2.2.5（下载 V21 cast 修复）与 2.2.7（V20 exe 真机自路由到 V21）记录的真机运行。
            { "DownloadToPlc", "CHANGELOG 2.2.5 / 2.2.7 V21 真机" },
            { "ConnectIsolated", "CHANGELOG 2.2.5 / 2.2.7 V21 真机" },
            // CHANGELOG 2.2.9 在活工程（5T 车）+ 测试 PLC 上实测的 SIMATIC SD 链路。
            // 只在 V20+ 版本门通过时才算 Verified（见 Classify）。
            { "ImportFromDocuments", "CHANGELOG 2.2.9 V20/V21 活工程实测" },
            { "ExportBlocksAsDocuments", "CHANGELOG 2.2.9 V20/V21 活工程实测" },
        };

        // Explicitly known-untested tools that do not fall under a token/gate rule below.
        // GetBlockInterface / GetTypeStructure：README.V19.md 2026-11-18 明确写着「尚未在 TIA V19
        // 真机上验证」，不能因为它们在 README 里出现过就当成 Verified。
        private static readonly HashSet<string> UnverifiedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetBlockInterface", "GetTypeStructure",
            // SD 文档工具中只有两个有 2.2.9 的活工程实测；这两个没有。
            "ExportAsDocuments", "ImportBlocksFromDocuments",
            // 真实 VCI 工具名（V21+）；版本门不足时先判 Planned，这里覆盖 V21 上「能用但未验证」。
            "GetVersionControlWorkspaces", "CreateVersionControlWorkspace",
            "GetVersionControlStatus", "SyncVersionControlWorkspace",
            "ConnectProjectToWorkspace",
        };

        // 显式列出真正声明了 bool dryRun 参数的工具（逐一核对过工具方法签名）。
        // 不能用「描述里出现 dryRun」来判：GetToolCapabilities 自己的说明里为了列前置条件
        // 也写了 dryRun，结果把自己标成需要 dryRun —— 一个只读元工具凭空多出一条前置。
        // 注意：DeletePlcExternalSource 和 ImportBlocksFromDirectory 没有 dryRun 参数；
        // 有 dryRun 的是 ImportPlcProgramFromDirectory。
        private static readonly HashSet<string> DryRunTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "DeletePlcBlock", "DeletePlcType", "DeletePlcTagTable",
            "DeletePlcTag", "DeletePlcWatchTable", "DeleteDevice",
            "SetDeviceItemIoAddress",
            "ScaffoldProject", "PlugDeviceItem", "PlcBuildAndImport", "ImportPlcProgramFromDirectory",
            "SyncVersionControlWorkspace", "ConnectProjectToWorkspace",
        };

        // HMI 全系在这个仓库的测试工程里跑不到（没有 HMI 设备），所以只能标 Unverified。
        // 一个 "Hmi" 子串已经覆盖 EnsureUnifiedHmi / CompileAndDiagnoseHmi 等所有变体。
        private const string HmiToken = "Hmi";

        private static readonly string[] SdDocumentTools =
        {
            "ExportAsDocuments", "ExportBlocksAsDocuments",
            "ImportFromDocuments", "ImportBlocksFromDocuments",
        };

        /// <summary>Read-only views for the offline suite. 两个集合必须不相交 —— 一个工具不可能
        /// 同时又「真机验证过」又「明确未验证」。暴露 internal 只给测试，不进 JSON。</summary>
        internal static IReadOnlyCollection<string> VerifiedToolNames => VerifiedTools.Keys;
        internal static IReadOnlyCollection<string> UnverifiedToolNames => UnverifiedTools;

        /// <summary>Classify one tool. <paramref name="tiaMajorVersion"/> 为 0 表示未知：
        /// 此时不做版本门控判断（不知道版本就不能断言「当前版本不支持」），但仍照常标
        /// Requires 里的 V20+/V21+。</summary>
        public static ToolCapabilityInfo Classify(string name, string description, int tiaMajorVersion = 0)
        {
            name = name ?? "";
            description = description ?? "";

            var safety = ToolSafety.Classify(name);

            ToolAccess access;
            if (safety.ReadOnly) access = ToolAccess.Read;
            else if (name.StartsWith("Delete", StringComparison.Ordinal)) access = ToolAccess.Delete;
            else access = ToolAccess.Write;

            bool sdDocument = IsSdDocumentTool(name);
            bool vci = IsVciTool(name);

            ToolStatus status;
            string evidence = "";
            if (sdDocument && tiaMajorVersion > 0 && tiaMajorVersion < 20)
            {
                // V19 构建里这些方法的版本判断会直接抛错；提前标 Planned，调用方就不会拿
                // 「工具存在」误当成「这台机器能做」。
                status = ToolStatus.Planned;
                evidence = SdDocumentsEvidence;
            }
            else if (vci && tiaMajorVersion > 0 && tiaMajorVersion < 21)
            {
                // V19/V20 构建通常已用 #if 排除了 VCI，但万一哪天漏了，能力矩阵仍要说实话。
                status = ToolStatus.Planned;
                evidence = VciEvidence;
            }
            else if (sdDocument && tiaMajorVersion < 20)
            {
                // 版本未知（0）时不能断言 Planned，但也没有任何实测记录可引用。
                status = ToolStatus.Unverified;
            }
            else if (VerifiedTools.TryGetValue(name, out var verifiedEvidence))
            {
                // SD 的 Verified 只在 V20+ 走到这里；V19 已被上面的 Planned 拦下。
                status = ToolStatus.Verified;
                evidence = verifiedEvidence;
            }
            else if (IsUnverified(name))
            {
                status = ToolStatus.Unverified;
            }
            else
            {
                status = ToolStatus.Implemented;
            }

            var requires = new List<string>();
            void Require(string item)
            {
                if (!requires.Contains(item)) requires.Add(item);
            }

            // 固定顺序：portal → online → confirm → dryRun → V20+ → V21+。
            if (safety.TouchesPortal) Require("portal");
            if (safety.OpenWorld) Require("online");
            if (safety.Destructive) Require("confirm");
            if (DryRunTools.Contains(name)) Require("dryRun");
            if (sdDocument) Require("V20+");
            if (vci) Require("V21+");

            return new ToolCapabilityInfo
            {
                Name = name,
                Category = ParseCategory(description),
                Access = access,
                Status = status,
                Destructive = safety.Destructive,
                DirectOnly = safety.DirectOnly,
                OpenWorld = safety.OpenWorld,
                Requires = requires.ToArray(),
                Evidence = evidence,
                Description = description,
            };
        }

        /// <summary>Classify a whole roster, preserving the caller's order. GetToolCapabilities
        /// 自己再排序，因为反射拿到的字典顺序不保证稳定。</summary>
        public static IReadOnlyList<ToolCapabilityInfo> All(
            IEnumerable<(string Name, string Description)> tools,
            int tiaMajorVersion = 0)
        {
            var list = new List<ToolCapabilityInfo>();
            if (tools == null) return list;
            foreach (var tool in tools)
                list.Add(Classify(tool.Name, tool.Description, tiaMajorVersion));
            return list;
        }

        /// <summary>从描述前缀取第二个方括号：`[L2][Category:PLC-Online]` → `PLC-Online`。
        /// 没有任何标签时用 General —— 宁可归到「杂项」，也不要把方括号里的层级当成类别。</summary>
        internal static string ParseCategory(string description)
        {
            if (string.IsNullOrEmpty(description)) return "General";
            string text = description.TrimStart();
            if (text.Length == 0 || text[0] != '[') return "General";

            int firstEnd = text.IndexOf(']');
            if (firstEnd < 0) return "General";

            int secondStart = text.IndexOf('[', firstEnd + 1);
            if (secondStart < 0) return "General";

            int secondEnd = text.IndexOf(']', secondStart + 1);
            if (secondEnd < 0) return "General";

            string category = text.Substring(secondStart + 1, secondEnd - secondStart - 1).Trim();
            const string categoryPrefix = "Category:";
            if (category.StartsWith(categoryPrefix, StringComparison.OrdinalIgnoreCase))
                category = category.Substring(categoryPrefix.Length).Trim();

            return category.Length == 0 ? "General" : category;
        }

        private static bool IsSdDocumentTool(string name)
        {
            foreach (var tool in SdDocumentTools)
                if (string.Equals(name, tool, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Real VCI tool names are GetVersionControlWorkspaces, CreateVersionControlWorkspace,
        /// GetVersionControlStatus, SyncVersionControlWorkspace and ConnectProjectToWorkspace.
        /// 用 Contains 而不是 StartsWith，是因为前四个都以 Get/Create/Sync 开头；Vci 前缀保留作防御。</summary>
        private static bool IsVciTool(string name)
        {
            return name.IndexOf("VersionControl", StringComparison.Ordinal) >= 0
                || name.StartsWith("Vci", StringComparison.Ordinal)
                || string.Equals(name, "ConnectProjectToWorkspace", StringComparison.Ordinal);
        }

        // Verified 优先于 Unverified：Classify 里已经先判过 VerifiedTools，这里才轮到「没验证过」。
        private static bool IsUnverified(string name)
        {
            if (name.IndexOf(HmiToken, StringComparison.Ordinal) >= 0) return true;
            if (UnverifiedTools.Contains(name)) return true;
            if (IsVciTool(name)) return true;
            return IsSdDocumentTool(name);
        }
    }

    /// <summary>
    /// Pure filtering / limit clamping for GetToolCapabilities. 抽成零依赖类是为了离线钉住：
    /// 过滤或夹取写错不会崩，只会让模型以为某个工具不存在（或者一次拿到整份 239 条工具）。
    /// </summary>
    public static class ToolCapabilityQuery
    {
        /// <summary>limit 语义（唯一一份，文档与测试都钉这里）：&lt;= 0 → 50；&gt; 500 → 500。
        /// 负数不是「不限量」，而是「调用方没想清楚」——回退到默认页，避免一次吐出全表。</summary>
        public const int DefaultLimit = 50;
        public const int MaxLimit = 500;

        public static int ClampLimit(int limit)
        {
            if (limit <= 0) return DefaultLimit;
            if (limit > MaxLimit) return MaxLimit;
            return limit;
        }

        /// <summary>
        /// Filter and page a capability roster.
        /// <paramref name="query"/>：空白分词，所有词都必须命中 name/description/category（大小写不敏感）。
        /// <paramref name="status"/> / <paramref name="access"/>：枚举名，大小写不敏感；非法值 → error 非空、空结果。
        /// <paramref name="total"/>：过滤后、截断前的命中总数；返回值为按名字排序后的当前页。
        /// 排序放在这里而不是调用方：反射字典顺序不稳定，测试和 diff 都需要确定性输出。
        /// </summary>
        public static IReadOnlyList<ToolCapabilityInfo> Filter(
            IEnumerable<ToolCapabilityInfo> capabilities,
            string? query,
            string? status,
            string? access,
            int limit,
            out int total,
            out string? error)
        {
            total = 0;
            error = null;

            if (!TryParseEnumName(status, out ToolStatus? statusFilter))
            {
                error = "Unknown status '" + status + "'. Valid values: "
                        + string.Join(", ", Enum.GetNames(typeof(ToolStatus))) + ".";
                return Array.Empty<ToolCapabilityInfo>();
            }
            if (!TryParseEnumName(access, out ToolAccess? accessFilter))
            {
                error = "Unknown access '" + access + "'. Valid values: "
                        + string.Join(", ", Enum.GetNames(typeof(ToolAccess))) + ".";
                return Array.Empty<ToolCapabilityInfo>();
            }

            string[] terms = SplitTerms(query);
            var matched = new List<ToolCapabilityInfo>();
            if (capabilities != null)
            {
                foreach (var capability in capabilities)
                {
                    if (capability == null) continue;
                    if (statusFilter.HasValue && capability.Status != statusFilter.Value) continue;
                    if (accessFilter.HasValue && capability.Access != accessFilter.Value) continue;
                    if (terms.Length > 0 && !Matches(capability, terms)) continue;
                    matched.Add(capability);
                }
            }

            // 反射字典的顺序不保证稳定；按名字排序，文本输出才能 diff、测试才能断言。
            matched.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            total = matched.Count;

            int take = ClampLimit(limit);
            if (take > matched.Count) take = matched.Count;
            var page = new List<ToolCapabilityInfo>(take);
            for (int i = 0; i < take; i++) page.Add(matched[i]);
            return page;
        }

        /// <summary>Filter values are enum NAMES looked up case-insensitively, not Enum.TryParse:
        /// TryParse also accepts "1" and out-of-range numbers, which the caller never meant.</summary>
        internal static bool TryParseEnumName<T>(string? raw, out T? value) where T : struct, Enum
        {
            value = null;
            if (string.IsNullOrWhiteSpace(raw)) return true;

            string text = raw!.Trim();
            foreach (var name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    value = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }
            return false;
        }

        internal static string[] SplitTerms(string? query)
        {
            var raw = (query ?? "")
                .Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return TrimToLower(raw);
        }

        private static string[] TrimToLower(string[] raw)
        {
            var list = new List<string>(raw.Length);
            foreach (var item in raw)
            {
                string term = item.Trim().ToLowerInvariant();
                if (term.Length > 0) list.Add(term);
            }
            return list.ToArray();
        }

        private static bool Matches(ToolCapabilityInfo capability, string[] terms)
        {
            string haystack = (capability.Name + "\n" + capability.Description + "\n" + capability.Category)
                .ToLowerInvariant();
            foreach (var term in terms)
            {
                if (haystack.IndexOf(term, StringComparison.Ordinal) < 0) return false;
            }
            return true;
        }
    }
}

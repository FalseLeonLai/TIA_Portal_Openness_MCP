using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// GetToolCapabilities 的返回体。Items 只放本次返回的一页，Total 是过滤后命中的总数
    /// （不受 limit 截断影响），调用方据此决定要不要翻页/缩小查询。
    /// 和 Responses.cs 里的其它返回体一样是命名空间级类型：MCP SDK 只要求公开 get/set
    /// 属性 + 无参构造，嵌套类型虽然也能序列化，但没必要和既有风格分叉。
    /// </summary>
    public sealed class ResponseToolCapabilities : ResponseMessage
    {
        public string TiaMajorVersion { get; set; } = "";
        public string Profile { get; set; } = "";
        public int Total { get; set; }
        public List<ToolCapabilityInfo> Items { get; set; } = new List<ToolCapabilityInfo>();
    }

    // 能力矩阵的查询入口。FindTools 只回答「有没有这个工具」，这里回答「能不能做、验没验过、
    // 需要什么前置」—— 模型在写批次或调用写工具之前，先用一次调用把这件事问清楚。
    public static partial class McpServer
    {
        [McpServerTool(Name = "GetToolCapabilities"), Description(
            "[L0][Meta] Describe what every tool does before calling it: read/write/delete access, " +
            "verification status (Verified / Implemented / Unverified / Planned), destructiveness, " +
            "direct-only calls, open-world calls, and prerequisites (portal, online, confirm, dryRun, V20+, V21+). " +
            "USE THIS before planning a multi-step edit or a write: a Planned tool cannot run on this TIA version, " +
            "and a Destructive tool needs the user's confirmation. " +
            "Filter by query (space-separated words matched against name/description/category), status or access; " +
            "limit defaults to 50 and caps at 500. Returns the same roster FindTools searches.")]
        public static ResponseToolCapabilities GetToolCapabilities(
            [Description("query: space-separated words matched against tool name, description and category, e.g. 'watch table export'. Empty lists everything.")] string query = "",
            [Description("status: optional filter - Verified, Implemented, Unverified or Planned (case-insensitive).")] string status = "",
            [Description("access: optional filter - Read, Write or Delete (case-insensitive).")] string access = "",
            [Description("limit: page size, default 50; values <= 0 use 50, values > 500 are capped at 500.")] int limit = 50)
        {
            try
            {
                // 版本未知（0）时写 "unknown"，而不是假装是 0 或猜一个默认版本：
                // 能力矩阵里最危险的就是把「不知道」说成一个确定值。
                string tiaVersion = Engineering.TiaMajorVersion > 0
                    ? Engineering.TiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "unknown";

                // 与 FindTools 用同一份反射目录、同一份描述，避免两个入口报出不同的能力。
                var catalog = AllToolMethods();
                var capabilities = new List<ToolCapabilityInfo>(catalog.Count);
                foreach (var kv in catalog)
                    capabilities.Add(ToolCapabilities.Classify(kv.Key, ToolDescription(kv.Value), Engineering.TiaMajorVersion));

                // 过滤/分词/夹取都在零依赖的 ToolCapabilityQuery 里，离线测试才能钉住边界。
                var items = ToolCapabilityQuery.Filter(
                    capabilities, query, status, access, limit, out int total, out string? filterError);
                if (filterError != null)
                {
                    return FilterError(filterError, tiaVersion);
                }

                string filters = DescribeFilters(status, access);
                bool hasQuery = ToolCapabilityQuery.SplitTerms(query).Length > 0;
                string message;
                if (total == 0)
                {
                    // 空查询时不要硬说 "query ''"：调用方只是给了个没有命中的过滤器。
                    if (hasQuery)
                        message = "No tool matches query '" + query + "'" + filters
                                  + ". Try fewer or more general words, or omit the filters.";
                    else if (filters.Length > 0)
                        message = "No tool matches the current filters" + filters
                                  + ". Try a different status/access or omit the filters.";
                    else
                        message = "The tool roster is empty.";
                }
                else
                {
                    message = "Returned " + items.Count + " of " + total + " matching tools"
                              + " (roster: " + catalog.Count + " total, profile: " + ResolvedProfile() + ")"
                              + filters + ".";
                }

                return new ResponseToolCapabilities
                {
                    Message = message,
                    TiaMajorVersion = tiaVersion,
                    Profile = ResolvedProfile(),
                    Total = total,
                    Items = items.ToList(),
                    Meta = BridgeMeta(true),
                };
            }
            catch (Exception ex)
            {
                return new ResponseToolCapabilities
                {
                    Message = "GetToolCapabilities failed: " + ex.Message,
                    TiaMajorVersion = Engineering.TiaMajorVersion > 0
                        ? Engineering.TiaMajorVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "unknown",
                    Profile = ResolvedProfile(),
                    Meta = BridgeMeta(false),
                };
            }
        }

        private static ResponseToolCapabilities FilterError(string message, string tiaVersion)
        {
            return new ResponseToolCapabilities
            {
                Message = message,
                TiaMajorVersion = tiaVersion,
                Profile = ResolvedProfile(),
                Total = 0,
                Items = new List<ToolCapabilityInfo>(),
                Meta = BridgeMeta(false),
            };
        }

        /// <summary>Filters are echoed as the caller wrote them (trimmed/lowercased) because the
        /// helper has already rejected anything that is not a real enum name.</summary>
        private static string DescribeFilters(string? status, string? access)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(status)) parts.Add("status=" + status!.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(access)) parts.Add("access=" + access!.Trim().ToLowerInvariant());
            return parts.Count == 0 ? "" : " (filters: " + string.Join(", ", parts) + ")";
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// 操作账本是审计和恢复的唯一依据。写坏了不会崩：只是事后无法判断「哪一步改了工程」，
    /// 或者在文件里留下明文参数（客户名、地址、变量值）。只有会失败的用例盯得住。
    /// </summary>
    internal static class OpsLedgerTests
    {
        internal static void Run(Action<bool, string> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "tia-mcp-ledger-tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "nested", "ops-ledger.jsonl");
            try
            {
                // ---- 文件不存在：空账本，不是错误 ----
                check(OpsLedger.ReadTail(path, 50).Count == 0,
                    "a missing ledger file reads as an empty list");

                // ---- append -> read tail：最后 N 行、按写入顺序 ----
                for (int i = 0; i < 3; i++)
                    OpsLedger.Append(path, Entry("batch-a", i, "GetBlocks", "ok", "{\"softwarePath\":\"PLC_" + i + "\"}"));

                check(File.Exists(path), "Append creates the directory and the ledger file");

                var all = OpsLedger.ReadTail(path, 50);
                check(all.Count == 3, "ReadTail returns every appended line (got " + all.Count + ")");
                check(all[0].Contains("\"index\":0") && all[2].Contains("\"index\":2"),
                    "ReadTail preserves append order");

                var tail2 = OpsLedger.ReadTail(path, 2);
                check(tail2.Count == 2 && tail2[0].Contains("\"index\":1") && tail2[1].Contains("\"index\":2"),
                    "ReadTail returns only the last N lines");

                // limit 语义：<=0 → 50，>500 → 500（这里只有 3 行，所以都返回 3 行）。
                check(OpsLedger.ReadTail(path, 0).Count == 3, "limit=0 falls back to 50");
                check(OpsLedger.ReadTail(path, -5).Count == 3, "a negative limit falls back to 50");
                check(OpsLedger.ReadTail(path, 501).Count == 3, "limit > 500 is clamped, not rejected");
                check(OpsLedger.ClampLimit(0) == 50 && OpsLedger.ClampLimit(501) == 500
                      && OpsLedger.ClampLimit(7) == 7,
                    "ClampLimit pins the 1..500 / default-50 semantics");

                // 每一行都是独立可解析的 JSON object。
                bool everyLineIsJson = all.All(line =>
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        return document.RootElement.ValueKind == JsonValueKind.Object;
                    }
                    catch (JsonException) { return false; }
                });
                check(everyLineIsJson, "every ledger line is a complete JSON object");

                // ---- SHA-256：稳定、64 位小写 hex、不同输入不同值 ----
                const string arguments = "{\"tagName\":\"Tag_1\",\"value\":42}";
                string first = OpsLedger.HashArguments(arguments);
                string second = OpsLedger.HashArguments(arguments);
                check(first == second, "HashArguments is stable for the same input");
                check(first.Length == 64, "HashArguments returns 64 hex characters (got " + first.Length + ")");
                check(first.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
                    "HashArguments is lowercase hex");
                check(first != OpsLedger.HashArguments(arguments + " "),
                    "a different arguments string produces a different hash");
                check(OpsLedger.HashArguments("") == OpsLedger.HashArguments(null),
                    "null and empty arguments hash the same way");
                // 空参数的 SHA-256 是固定常量；钉住它可以发现「哈希的是规范化对象而不是原始字符串」这类回归。
                check(OpsLedger.HashArguments("") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                    "empty arguments hash to the well-known SHA-256 of zero bytes");

                // ---- 不落明文参数：文件里只有哈希 ----
                const string secret = "plaintext-xyz-SECRET";
                string secretArgs = "{\"password\":\"" + secret + "\"}";
                OpsLedger.Append(path, Entry("batch-secret", 0, "SetPlcTag", "ok", secretArgs));
                string file = File.ReadAllText(path);
                check(!file.Contains(secret), "plain-text arguments never reach the ledger");
                check(file.Contains(OpsLedger.HashArguments(secretArgs)),
                    "the ledger keeps the SHA-256 of the arguments instead");

                // ---- 异常消息的脱敏：工具异常常原样引用标签名/路径，不能从 error 字段漏出 ----
                const string tagName = "CustomerValve_PLAINTEXT";
                const string commentSecret = "SECRET-COMMENT-VALUE";
                string redactArgs = "{\"softwarePath\":\"PLC_1\",\"tagName\":\"" + tagName
                                    + "\",\"nested\":{\"comment\":\"" + commentSecret + "\"}}";
                string? redacted = OpsLedger.RedactError(
                    "McpException: tag '" + tagName + "' was not found; comment " + commentSecret
                    + "; path C:\\Projects\\Plant", redactArgs);
                check(redacted != null && !redacted.Contains(tagName) && !redacted.Contains(commentSecret),
                    "RedactError removes every string value from the arguments JSON (nested included)");
                check(redacted != null && redacted.Contains("***")
                      && redacted.Contains("McpException") && redacted.Contains("was not found"),
                    "RedactError keeps the exception type/message shape and marks the removed values");

                string? longRedacted = OpsLedger.RedactError(new string('x', 400), "");
                check(longRedacted != null && longRedacted.Length == OpsLedger.MaxErrorLength,
                    "RedactError truncates errors to 300 characters");
                string? invalidJsonRedacted = OpsLedger.RedactError(new string('y', 400), "{not json");
                check(invalidJsonRedacted != null && invalidJsonRedacted.Length == OpsLedger.MaxErrorLength,
                    "invalid arguments JSON still truncates instead of throwing");
                check(OpsLedger.RedactError(null, redactArgs) == null,
                    "a successful operation keeps a null error");

                // 数字和布尔值也是参数值。异常消息常常原样引用它们（"value 42424242 out of
                // range"），只替换字符串会让数字和布尔值原样落盘。
                string? numericRedacted = OpsLedger.RedactError(
                    "McpException: value 42424242 out of range (limit 100)",
                    "{\"value\":42424242,\"limit\":100}");
                check(numericRedacted != null
                      && !numericRedacted.Contains("42424242") && !numericRedacted.Contains("100")
                      && numericRedacted.Contains("***"),
                    "RedactError removes numeric scalar values too");

                string? booleanRedacted = OpsLedger.RedactError(
                    "McpException: flag true was rejected", "{\"flag\":true}");
                check(booleanRedacted != null
                      && !booleanRedacted.Contains("true") && booleanRedacted.Contains("***"),
                    "RedactError removes boolean scalar values too");

                // 异常消息里的值常由 .NET 绑定后渲染，而不是 JSON 原文：布尔是 True/False，
                // 数字可能是整型、带小数或科学计数。只替换 GetRawText 会漏掉这些等价形式。
                string? booleanDotNetRedacted = OpsLedger.RedactError(
                    "McpException: flag True was rejected", "{\"flag\":true}");
                check(booleanDotNetRedacted != null
                      && !booleanDotNetRedacted.Contains("True")
                      && booleanDotNetRedacted.Contains("***"),
                    "RedactError removes the .NET-rendered True form of a boolean argument");

                string? decimalRedacted = OpsLedger.RedactError(
                    "McpException: value 42424242 out of range", "{\"value\":42424242.0}");
                check(decimalRedacted != null
                      && !decimalRedacted.Contains("42424242")
                      && decimalRedacted.Contains("***"),
                    "RedactError removes a number written with a trailing .0");

                string? exponentRedacted = OpsLedger.RedactError(
                    "McpException: value 42424242 out of range", "{\"value\":4.2424242E7}");
                check(exponentRedacted != null
                      && !exponentRedacted.Contains("42424242")
                      && exponentRedacted.Contains("***"),
                    "RedactError removes a number written in scientific notation");

                string? negativeRedacted = OpsLedger.RedactError(
                    "McpException: value -42424242 out of range", "{\"value\":-42424242.0}");
                check(negativeRedacted != null
                      && !negativeRedacted.Contains("42424242")
                      && !negativeRedacted.Contains("-42424242")
                      && negativeRedacted.Contains("***"),
                    "RedactError removes a negative number written with a trailing .0");

                // 嵌套数组/对象里的每种标量都要走同一套替换。
                const string nestedArgs = "{\"items\":[{\"inner\":\"NESTED-SECRET\",\"count\":987654}],\"enabled\":false}";
                string? nestedRedacted = OpsLedger.RedactError(
                    "McpException: NESTED-SECRET count 987654 enabled false", nestedArgs);
                check(nestedRedacted != null
                      && !nestedRedacted.Contains("NESTED-SECRET")
                      && !nestedRedacted.Contains("987654")
                      && !nestedRedacted.Contains("false"),
                    "RedactError walks nested arrays/objects and redacts every scalar kind");

                // 值跨在 300 字符边界上：先截断再替换会留下明文前缀。必须先替换、后截断。
                const string straddling = "STRADDLE-SECRET-VALUE-987";
                string boundaryError = new string('a', OpsLedger.MaxErrorLength - 5)
                                       + straddling + new string('b', 40);
                string? boundaryRedacted = OpsLedger.RedactError(
                    boundaryError, "{\"name\":\"" + straddling + "\"}");
                check(boundaryRedacted != null
                      && boundaryRedacted.Length <= OpsLedger.MaxErrorLength
                      && !boundaryRedacted.Contains("STRADDLE"),
                    "a value crossing the 300-character boundary is replaced before truncation");

                // 单字符值替换成 "***" 会变长；先替换后截断才能保证最终长度不超上限。
                string? expanded = OpsLedger.RedactError(
                    new string('1', OpsLedger.MaxErrorLength), "{\"n\":1}");
                check(expanded != null && expanded.Length <= OpsLedger.MaxErrorLength,
                    "replacement cannot expand the error past the 300-character cap");
                check(OpsLedger.RedactError(new string('z', 400), "{not json")!.Length
                      == OpsLedger.MaxErrorLength,
                    "invalid JSON is truncated without attempting redaction");

                string redactedPath = Path.Combine(root, "redacted.jsonl");
                OpsLedger.Append(redactedPath, Entry(
                    "batch-redact", 0, "SetPlcTag", "error", redactArgs, redacted));
                string redactedFile = File.ReadAllText(redactedPath);
                check(!redactedFile.Contains(tagName),
                    "a tool exception cannot leak the tag name through the ledger error field");
                check(redactedFile.Contains("***"),
                    "the ledger keeps the redacted error instead of the plaintext one");

                // ---- 没有结尾换行：ReadTail 的 ReadLine 循环必须照样读到最后一行 ----
                string noNewline = Path.Combine(root, "no-trailing-newline.jsonl");
                File.WriteAllText(noNewline, "{\"index\":0}\n{\"index\":1}");
                var noNewlineTail = OpsLedger.ReadTail(noNewline, 500);
                check(noNewlineTail.Count == 2 && noNewlineTail[1].Contains("\"index\":1"),
                    "ReadTail with limit=500 reads a file whose last line has no trailing newline");

                // ---- 跨进程共享：另一个宿主可能正以读写方式持有账本 ----
                string shared = Path.Combine(root, "shared.jsonl");
                OpsLedger.Append(shared, Entry("batch-shared", 0, "GetBlocks", "ok", "{}"));
                using (var holder = new FileStream(shared, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    OpsLedger.Append(shared, Entry("batch-shared", 1, "GetBlocks", "ok", "{}"));
                    holder.Position = 0;
                    using var reader = new StreamReader(holder);
                    string content = reader.ReadToEnd();
                    check(content.Split('\n').Count(line => line.Trim().Length > 0) == 2,
                        "Append opens with FileShare.ReadWrite so another process can append too");
                }

                // ---- 并发追加：锁保证每一行完整、计数不丢 ----
                string concurrent = Path.Combine(root, "concurrent.jsonl");
                const int writers = 4;
                const int perWriter = 25;
                Parallel.For(0, writers, w =>
                {
                    for (int i = 0; i < perWriter; i++)
                        OpsLedger.Append(concurrent, Entry("batch-c" + w, i, "GetBlocks", "ok", "{}"));
                });
                var concurrentLines = OpsLedger.ReadTail(concurrent, 500);
                check(concurrentLines.Count == writers * perWriter,
                    "concurrent appends lose no lines (got " + concurrentLines.Count + " of " + (writers * perWriter) + ")");
                check(concurrentLines.All(line =>
                {
                    try { using var document = JsonDocument.Parse(line); return true; }
                    catch (JsonException) { return false; }
                }), "concurrent appends never interleave into a broken line");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { /* best effort: a locked temp file must not fail the suite */ }
            }
        }

        private static OpsLedgerEntry Entry(
            string batchId, int index, string tool, string status, string argumentsJson,
            string? error = null)
            => new OpsLedgerEntry
            {
                Timestamp = "2026-11-19T10:00:00+08:00",
                BatchId = batchId,
                Index = index,
                Tool = tool,
                Access = "Read",
                Status = status,
                DurationMs = index * 10,
                ArgumentsSha256 = OpsLedger.HashArguments(argumentsJson),
                Error = error,
            };
    }
}

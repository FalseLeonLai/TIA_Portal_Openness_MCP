using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// One line in the operation ledger. Deliberately stores a SHA-256 of the raw arguments,
    /// never the arguments themselves: the ledger is an audit trail that may outlive the
    /// session, and TIA projects routinely contain customer names, addresses and tag values.
    /// </summary>
    public sealed class OpsLedgerEntry
    {
        public string Timestamp { get; set; } = "";
        public string BatchId { get; set; } = "";
        public int Index { get; set; }
        public string Tool { get; set; } = "";
        public string Access { get; set; } = "";
        /// <summary>ok / error / skipped — exactly these three values.</summary>
        public string Status { get; set; } = "";
        public long DurationMs { get; set; }
        /// <summary>Lowercase hex SHA-256 of the raw ArgumentsJson. When there were no arguments
        /// this is the SHA-256 of the empty string (not empty).</summary>
        public string ArgumentsSha256 { get; set; } = "";
        public string? Error { get; set; }
    }

    /// <summary>
    /// Append-only JSONL operation ledger, independent of the MCP SDK and TIA Portal.
    ///
    /// 为什么需要锁：RunBatch 内部已经把批次串行化了，但 GetOpsLedger 可能在另一个
    /// 请求里同时读，宿主也可能并发调用多个批次（在进入批次锁之前）。追加和读取共用
    /// 同一把进程内锁，保证不会读到半行，也不会让两个 writer 交错写坏一行 JSON。
    /// </summary>
    public static class OpsLedger
    {
        public const int DefaultLimit = 50;
        public const int MaxLimit = 500;
        public const string PathEnvironmentVariable = "TIA_MCP_LEDGER";

        private static readonly object Gate = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // camelCase 与 MCP 响应保持一致，GetOpsLedger 把原始行直接交给模型/宿主。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>TIA_MCP_LEDGER wins so tests and CI can redirect the ledger; otherwise the
        /// per-user local application data folder (%LOCALAPPDATA%\TiaMcpServer\ops-ledger.jsonl).</summary>
        public static string DefaultPath
        {
            get
            {
                string? overridden = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(overridden)) return overridden!.Trim();

                string? root = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (string.IsNullOrWhiteSpace(root))
                    root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(root)) root = ".";

                return Path.Combine(root!, "TiaMcpServer", "ops-ledger.jsonl");
            }
        }

        /// <summary>Append one UTF-8 JSON line, creating the directory when needed.
        /// The write is a single FileMode.Append + explicit '\n', so a reader that shares
        /// FileShare.ReadWrite sees either the old file or a complete new line.</summary>
        public static void Append(string path, OpsLedgerEntry entry)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("ledger path is required", nameof(path));
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            string line = JsonSerializer.Serialize(entry, JsonOptions);

            lock (Gate)
            {
                string full = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // UTF-8 without BOM: a BOM in the middle of an append-only file would corrupt
                // line-oriented readers after the first write from another process.
                // FileShare.ReadWrite: the ledger is append-only and may have another process
                // (another MCP host, a tail -f, a log shipper) holding it open for read/write.
                // FileShare.Read alone made that second appender fail with a sharing violation.
                using (var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(line);
                    writer.Write('\n');
                    writer.Flush();
                }
            }
        }

        /// <summary>Last <paramref name="limit"/> non-empty lines. A missing file is an empty
        /// ledger, not an error: GetOpsLedger is called before the first batch all the time.</summary>
        public static IReadOnlyList<string> ReadTail(string path, int limit)
        {
            int take = ClampLimit(limit);
            if (string.IsNullOrWhiteSpace(path)) return Array.Empty<string>();

            lock (Gate)
            {
                string full = Path.GetFullPath(path);
                if (!File.Exists(full)) return Array.Empty<string>();

                var tail = new Queue<string>(take);
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        tail.Enqueue(line);
                        while (tail.Count > take) tail.Dequeue();
                    }
                }
                return new List<string>(tail);
            }
        }

        /// <summary>SHA-256 of the raw arguments string, lowercase hex. Hashing the raw text
        /// (rather than a re-serialized object) makes the ledger entry reproducible by the
        /// caller from the exact bytes it submitted.</summary>
        public static string HashArguments(string? argumentsJson)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(argumentsJson ?? "");
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(bytes);

            var builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                builder.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        /// <summary>Upper bound on the redacted error text. Redaction runs first, then the result
        /// is truncated to this many characters: the other order would leave a sensitive value
        /// that straddles the boundary as a plaintext prefix. Tool exceptions sometimes embed an
        /// entire project path plus the values that were rejected; the ledger only needs the
        /// shape of the failure, not the payload.</summary>
        public const int MaxErrorLength = 300;

        /// <summary>
        /// Make an exception message safe to persist. Tool exceptions routinely quote the exact
        /// tag/block/path the caller passed (e.g. "tag 'CustomerValve' was not found"), so even a
        /// ledger that stores only the arguments hash can leak plaintext through <c>error</c>.
        ///
        /// Rules, in order:
        /// 1. replace every scalar value found in <paramref name="argumentsJson"/> with "***"
        ///    (strings, numbers and true/false; nested objects and arrays included), longest
        ///    textual form first so a value that is a substring of another is not damaged before
        ///    the longer match runs;
        /// 2. only then truncate the redacted text to <see cref="MaxErrorLength"/> characters.
        ///    Truncating first would let a value that straddles the boundary survive as a prefix,
        ///    and a one-character value expands to three '*' so the cap must be applied after
        ///    replacement anyway;
        /// 3. if the arguments are not valid JSON, do step 2 only — there is no reliable way to
        ///    know which fragments are sensitive, and guessing could mangle the exception text.
        /// </summary>
        public static string? RedactError(string? error, string? argumentsJson)
        {
            if (error == null) return null;

            if (string.IsNullOrWhiteSpace(argumentsJson)) return Truncate(error);

            var values = new List<string>();
            try
            {
                using (var document = JsonDocument.Parse(argumentsJson!))
                    CollectScalarTexts(document.RootElement, values);
            }
            catch (JsonException)
            {
                // 参数本身不是合法 JSON：只截断，不再猜哪些片段敏感。
                return Truncate(error);
            }

            // 长的先替换：参数 "Default tag table" 和 "Tag" 同时出现时，先换短的会把长的
            // 拆成 "Default *** table"，反而制造出新的半明文片段。
            values.Sort((a, b) => b.Length.CompareTo(a.Length));
            string text = error;
            foreach (var value in values)
                text = text.Replace(value, "***");

            return Truncate(text);
        }

        private static string Truncate(string text)
            => text.Length > MaxErrorLength ? text.Substring(0, MaxErrorLength) : text;

        private static void CollectScalarTexts(JsonElement element, List<string> values)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        CollectScalarTexts(property.Value, values);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        CollectScalarTexts(item, values);
                    break;
                case JsonValueKind.String:
                    string? value = element.GetString();
                    // 空串替换会把整段文字变成 ***，也泄露不了任何东西。
                    if (!string.IsNullOrEmpty(value)) values.Add(value!);
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    // GetRawText 返回参数里实际出现的字面量（true / false）。
                    // 异常通常由 .NET 绑定后的值渲染，布尔会写成 True / False，所以两种
                    // 形式都要替换；只加 raw 的话 "flag True was rejected" 仍会泄漏。
                    string boolRaw = element.GetRawText();
                    if (!string.IsNullOrEmpty(boolRaw)) values.Add(boolRaw);
                    values.Add(element.GetBoolean() ? "True" : "False");
                    break;
                case JsonValueKind.Number:
                    // 数字在异常里可能以任何文本形式出现：原文（42424242）、带小数
                    // （42424242.0）、科学计数（4.2424242E7），或 .NET 绑定后渲染的
                    // 整型/浮点形式。原文之外再补充 int64 和 "R" 往返格式，才能覆盖
                    // 这些等价写法；只加 raw 会漏掉后三种。
                    string numberRaw = element.GetRawText();
                    if (!string.IsNullOrEmpty(numberRaw)) values.Add(numberRaw);
                    if (element.TryGetInt64(out long integer))
                        values.Add(integer.ToString(CultureInfo.InvariantCulture));
                    if (element.TryGetDouble(out double number))
                        values.Add(number.ToString("R", CultureInfo.InvariantCulture));
                    break;
            }
        }

        /// <summary>Same limit semantics as every other tail/list tool here:
        /// &lt;= 0 → 50, &gt; 500 → 500.</summary>
        public static int ClampLimit(int limit)
        {
            if (limit <= 0) return DefaultLimit;
            if (limit > MaxLimit) return MaxLimit;
            return limit;
        }
    }
}

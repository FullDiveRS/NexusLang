using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Lsp
{
    /// <summary>
    /// Minimal JSON-RPC 2.0 transport over streams using LSP Content-Length
    /// framing. Built entirely on System.Text.Json (BCL) — zero external deps.
    /// </summary>
    public static class LspJson
    {
        /// <summary>Read one framed message. Returns null when the stream closes.</summary>
        public static JsonObject ReadMessage(Stream input)
        {
            int contentLength = -1;

            while (true)
            {
                string line = ReadHeaderLine(input);
                if (line == null) return null;          // stream closed
                if (line.Length == 0) break;            // blank line = end of headers

                int idx = line.IndexOf(':');
                if (idx > 0 &&
                    line.Substring(0, idx).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    !int.TryParse(line.Substring(idx + 1).Trim(), out contentLength))
                {
                    contentLength = -1;
                }
            }

            if (contentLength < 0 || contentLength > 64_000_000) return null;

            var body = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = input.Read(body, read, contentLength - read);
                if (n <= 0) return null;
                read += n;
            }

            try
            {
                return JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string ReadHeaderLine(Stream input)
        {
            var sb = new StringBuilder();
            while (true)
            {
                int b = input.ReadByte();
                if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
                if (b == '\n')
                {
                    return sb.Length > 0 && sb[sb.Length - 1] == '\r'
                        ? sb.ToString(0, sb.Length - 1)
                        : sb.ToString();
                }
                sb.Append((char)b);
                if (sb.Length > 4096) return sb.ToString();   // header sanity cap
            }
        }

        /// <summary>Write one framed message and flush.</summary>
        public static void WriteMessage(Stream output, JsonObject message)
        {
            string json = JsonSerializer.Serialize(message);
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            output.Write(header, 0, header.Length);
            output.Write(body, 0, body.Length);
            output.Flush();
        }

        // ===== small helpers (JsonNode can only have one parent — always copy) =====

        public static JsonNode Copy(JsonNode node) => node?.DeepClone();

        /// <summary>Extract a request id as a fresh node (number or string).</summary>
        public static JsonNode ExtractId(JsonObject msg)
        {
            var id = msg["id"];
            if (id == null) return null;
            return id.GetValueKind() == JsonValueKind.Number
                ? JsonValue.Create(id.GetValue<int>())
                : JsonValue.Create(id.ToJsonString().Trim('"'));
        }

        public static string GetString(JsonObject msg, string key) =>
            msg[key]?.ToString();

        public static long GetLong(JsonObject msg, string key)
        {
            var n = msg[key];
            if (n == null) return 0;
            return n.GetValueKind() == JsonValueKind.Number ? n.GetValue<long>()
                 : long.TryParse(n.ToJsonString().Trim('"'), out var v) ? v : 0;
        }

        public static JsonObject GetObj(JsonObject msg, string key) =>
            msg[key] as JsonObject;

        /// <summary>Build an LSP position object (0-based line/character).</summary>
        public static JsonObject Pos(int line, int character) => new JsonObject
        {
            ["line"] = Math.Max(0, line),
            ["character"] = Math.Max(0, character)
        };

        public static JsonObject Range(int sl, int sc, int el, int ec) => new JsonObject
        {
            ["start"] = Pos(sl, sc),
            ["end"] = Pos(el, ec)
        };

        public static JsonObject Location(string uri, int sl, int sc, int el, int ec) => new JsonObject
        {
            ["uri"] = uri,
            ["range"] = Range(sl, sc, el, ec)
        };

        public static JsonObject Markdown(string value) => new JsonObject
        {
            ["kind"] = "markdown",
            ["value"] = value
        };
    }
}

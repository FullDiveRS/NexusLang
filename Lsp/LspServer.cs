using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace Nexus.Lsp
{
    /// <summary>
    /// The Nexus Language Server: a single-threaded JSON-RPC/LSP server over stdio.
    /// Provides diagnostics (real lexer/parser), context-aware completion, hover,
    /// go-to-definition and document symbols. Logs go to stderr (or --log=file).
    /// IMPORTANT: stdout carries ONLY LSP frames — never print anything else.
    /// </summary>
    public class LspServer
    {
        private readonly NexusWorkspace _ws = new();
        private Stream _output;
        private StreamWriter _log;
        private bool _shutdownRequested;

        public void Run(Stream input, Stream output, string logPath = null)
        {
            _output = output;
            _log = OpenLog(logPath);

            Log("Nexus language server starting");

            while (true)
            {
                JsonObject msg;
                try
                {
                    msg = LspJson.ReadMessage(input);
                }
                catch (Exception ex)
                {
                    Log($"read error: {ex.Message}");
                    break;
                }
                if (msg == null) break;    // stream closed

                var sw = Stopwatch.StartNew();
                try
                {
                    Dispatch(msg);
                }
                catch (Exception ex)
                {
                    Log($"dispatch error: {ex}");
                    var id = msg.ContainsKey("id") ? LspJson.ExtractId(msg) : null;
                    if (id != null)
                        SendError(id, -32603, $"Internal error: {ex.Message}");
                }
                var method = LspJson.GetString(msg, "method") ?? "(response)";
                if (sw.ElapsedMilliseconds > 50)
                    Log($"SLOW {method}: {sw.ElapsedMilliseconds} ms");
            }

            Log("stream closed — exiting");
        }

        private static StreamWriter OpenLog(string logPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(logPath))
                {
                    var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    return new StreamWriter(fs) { AutoFlush = true };
                }
            }
            catch { }
            return null;
        }

        private void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            if (_log != null) _log.WriteLine(line);
            Console.Error.WriteLine(line);   // stderr — never interferes with LSP
        }

        // ============ dispatch ============
        private void Dispatch(JsonObject msg)
        {
            string method = LspJson.GetString(msg, "method");
            var id = msg.ContainsKey("id") ? LspJson.ExtractId(msg) : null;
            var @params = LspJson.GetObj(msg, "params");

            if (method == null)
                return;    // a response from the client — ignore

            if (id == null)
            {
                HandleNotification(method, @params);
                return;
            }

            switch (method)
            {
                case "initialize":
                    SendResult(id, Initialize(@params));
                    break;
                case "shutdown":
                    _shutdownRequested = true;
                    SendResult(id, null);
                    break;
                case "textDocument/completion":
                    WithDoc(id, @params, doc =>
                    {
                        var p = LspJson.GetObj(@params, "position");
                        return CompletionEngine.Complete(_ws, doc,
                            (int)LspJson.GetLong(p, "line"), (int)LspJson.GetLong(p, "character"));
                    });
                    break;
                case "textDocument/hover":
                    WithDoc(id, @params, doc =>
                    {
                        var p = LspJson.GetObj(@params, "position");
                        return HoverEngine.Hover(_ws, doc,
                            (int)LspJson.GetLong(p, "line"), (int)LspJson.GetLong(p, "character"));
                    });
                    break;
                case "textDocument/definition":
                    WithDoc(id, @params, doc =>
                    {
                        var p = LspJson.GetObj(@params, "position");
                        var arr = SymbolEngine.Definitions(_ws, doc,
                            (int)LspJson.GetLong(p, "line"), (int)LspJson.GetLong(p, "character"));
                        return arr.Count > 0 ? (JsonNode)arr : null;
                    });
                    break;
                case "textDocument/documentSymbol":
                    WithDoc(id, @params, doc => (JsonNode)SymbolEngine.DocumentSymbols(doc));
                    break;
                case "workspace/symbol":
                {
                    try { _ws.EnsureIndexed(); } catch { }
                    var queryParams = LspJson.GetObj(msg, "params");
                    var query = (LspJson.GetString(queryParams, "query") ?? "").ToLowerInvariant();
                    var arr = new JsonArray();
                    foreach (var s in _ws.AllSymbols())
                    {
                        if (s.Kind == "operator") continue;
                        if (query.Length > 0 && !s.Name.ToLowerInvariant().Contains(query)) continue;
                        var text = DocText(s.Uri);
                        var (sl, sc) = NodePos(text, s.Line, s.Column);
                        arr.Add(new JsonObject
                        {
                            ["name"] = s.Name,
                            ["kind"] = KindNumber(s.Kind),
                            ["location"] = LspJson.Location(s.Uri, sl, sc, sl, sc + Math.Max(1, s.Name.Length)),
                            ["containerName"] = s.Detail ?? s.Kind
                        });
                    }
                    SendResult(id, arr);
                    break;
                }
                default:
                    SendError(id, -32601, $"Method not found: {method}");
                    break;
            }
        }

        private void HandleNotification(string method, JsonObject @params)
        {
            switch (method)
            {
                case "initialized":
                    Log("client initialized");
                    break;
                case "exit":
                    Environment.Exit(_shutdownRequested ? 0 : 1);
                    break;
                case "textDocument/didOpen":
                {
                    var td = LspJson.GetObj(@params, "textDocument");
                    var uri = LspJson.GetString(td, "uri");
                    var text = LspJson.GetString(td, "text");
                    var version = LspJson.GetLong(td, "version");
                    _ws.Open(new NexusDocument(uri, text, version));
                    Log($"didOpen {uri} ({text?.Length ?? 0} chars)");
                    PublishDiagnostics(uri);
                    break;
                }
                case "textDocument/didChange":
                {
                    var td = LspJson.GetObj(@params, "textDocument");
                    var uri = LspJson.GetString(td, "uri");
                    var version = LspJson.GetLong(td, "version");
                    var doc = _ws.Get(uri);
                    var changes = @params["contentChanges"] as JsonArray;
                    string text = changes != null && changes.Count > 0
                        ? changes[changes.Count - 1]?["text"]?.ToString() : null;
                    if (doc != null && text != null)
                        doc.Update(text, version);
                    else if (doc == null && text != null)
                        _ws.Open(new NexusDocument(uri, text, version));
                    PublishDiagnostics(uri);
                    break;
                }
                case "textDocument/didClose":
                {
                    var td = LspJson.GetObj(@params, "textDocument");
                    var uri = LspJson.GetString(td, "uri");
                    _ws.Close(uri);
                    ClearDiagnostics(uri);
                    break;
                }
                case "textDocument/didSave":
                case "workspace/didChangeConfiguration":
                case "workspace/didChangeWatchedFiles":
                case "workspace/didChangeWorkspaceFolders":
                case "$/setTrace":
                case "$/cancelRequest":
                case "$/progress":
                    break;
                default:
                    Log($"ignoring notification {method}");
                    break;
            }
        }

        // ============ handlers ============
        private JsonObject Initialize(JsonObject @params)
        {
            _ws.RootPath = null;
            try
            {
                var rootUri = LspJson.GetString(@params, "rootUri");
                if (!string.IsNullOrEmpty(rootUri) && rootUri.StartsWith("file://"))
                    _ws.RootPath = new Uri(rootUri).LocalPath;
                else
                {
                    var folders = @params?["workspaceFolders"] as JsonArray;
                    var f0 = folders != null && folders.Count > 0 ? folders[0]?["uri"]?.ToString() : null;
                    if (!string.IsNullOrEmpty(f0) && f0.StartsWith("file://"))
                        _ws.RootPath = new Uri(f0).LocalPath;
                }
            }
            catch (Exception ex)
            {
                Log($"rootUri parse failed: {ex.Message}");
            }
            if (!string.IsNullOrEmpty(_ws.RootPath))
                Log($"workspace root: {_ws.RootPath}");

            return new JsonObject
            {
                ["capabilities"] = new JsonObject
                {
                    ["textDocumentSync"] = new JsonObject
                    {
                        ["openClose"] = true,
                        ["change"] = 1        // Full
                    },
                    ["completionProvider"] = new JsonObject
                    {
                        ["triggerCharacters"] = new JsonArray { ".", ":" },
                        ["resolveProvider"] = false
                    },
                    ["hoverProvider"] = true,
                    ["definitionProvider"] = true,
                    ["documentSymbolProvider"] = true,
                    ["workspaceSymbolProvider"] = true
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "Nexus Language Server",
                    ["version"] = "1.1.0"
                }
            };
        }

        private void PublishDiagnostics(string uri)
        {
            var doc = _ws.Get(uri);
            var diags = doc?.ToDiagnosticJson() ?? new JsonArray();
            SendNotification("textDocument/publishDiagnostics", new JsonObject
            {
                ["uri"] = uri,
                ["diagnostics"] = diags
            });
        }

        private void ClearDiagnostics(string uri)
        {
            SendNotification("textDocument/publishDiagnostics", new JsonObject
            {
                ["uri"] = uri,
                ["diagnostics"] = new JsonArray()
            });
        }

        private string DocText(string uri)
        {
            var d = _ws.Get(uri);
            if (d != null) return d.Text;
            try
            {
                if (uri.StartsWith("file://"))
                    return File.ReadAllText(new Uri(uri).LocalPath);
            }
            catch { }
            return "";
        }

        private static (int, int) NodePos(string text, int oneBasedLine, int oneBasedColumn) =>
            LspText.PositionAt(text ?? "",
                LspText.OffsetAt(text ?? "", Math.Max(0, oneBasedLine - 1), Math.Max(0, oneBasedColumn - 1)));

        private static int KindNumber(string kind) => kind switch
        {
            "class" => 5, "method" => 6, "property" => 7, "field" => 8, "enum" => 10,
            "function" => 12, "var" => 13, "constant" => 14, "enumMember" => 22,
            "struct" => 23, "event" => 24, "delegate" => 12, "asmModule" => 2,
            "namespace" => 3, _ => 13
        };

        // ============ send helpers ============
        private void WithDoc<T>(JsonNode id, JsonObject @params, Func<NexusDocument, T> handler)
        {
            var td = LspJson.GetObj(@params, "textDocument");
            var uri = LspJson.GetString(td, "uri");
            var doc = _ws.Get(uri);
            if (doc == null)
            {
                // not open (e.g. request raced with didOpen) — try disk
                doc = new NexusDocument(uri, DocText(uri), 0);
                _ws.Open(doc);
            }
            SendResult(id, handler(doc) as JsonNode);
        }

        private void SendResult(JsonNode id, JsonNode result)
        {
            var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = LspJson.Copy(id) };
            resp["result"] = result;
            LspJson.WriteMessage(_output, resp);
        }

        private void SendError(JsonNode id, int code, string message)
        {
            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = LspJson.Copy(id),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
            };
            LspJson.WriteMessage(_output, resp);
        }

        private void SendNotification(string method, JsonObject @params)
        {
            var msg = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = @params
            };
            LspJson.WriteMessage(_output, msg);
        }
    }
}

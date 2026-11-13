using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Nexus.Core;

namespace Nexus.Lsp
{
    /// <summary>A diagnostic in 0-based LSP coordinates.</summary>
    public class DocDiagnostic
    {
        public int Line, Character, EndLine, EndCharacter;
        public int Severity;          // 1=error 2=warning 3=info
        public string Code, Message;
    }

    /// <summary>An analyzed Nexus source document: text + tokens + AST + diagnostics.
    /// Re-analyzes (real Lexer + real Parser) on every open/change — the parse is
    /// error-recovering, so broken code still yields partial ASTs and diagnostics.</summary>
    public class NexusDocument
    {
        private static readonly Regex LinePrefix = new(@"^\s*Line (\d+):\s*(.*)$", RegexOptions.Compiled);

        public string Uri { get; }
        public string Text { get; private set; }
        public long Version { get; private set; }
        public List<Token> Tokens { get; private set; } = new();
        public ProgramNode Program { get; private set; }
        public List<DocDiagnostic> Diagnostics { get; private set; } = new();
        public DateTime AnalyzedAt { get; private set; } = DateTime.UtcNow;

        public NexusDocument(string uri, string text, long version)
        {
            Uri = uri;
            Update(text, version);
        }

        public void Update(string text, long version)
        {
            Text = text ?? "";
            Version = version;
            Analyze();
        }

        private void Analyze()
        {
            var diags = new List<DocDiagnostic>();
            Tokens = new List<Token>();
            Program = new ProgramNode();

            try
            {
                var lexer = new Lexer(Text);
                Tokens = lexer.Tokenize();
                foreach (var e in lexer.Errors) Add(diags, e, 1, "lex");

                var parser = new Parser(Tokens);
                Program = parser.ParseProgram();
                foreach (var e in parser.Errors) Add(diags, e, 1, "parse");
            }
            catch (Exception ex)
            {
                // defensive: analyzer must never take the server down
                Add(diags, $"Line 1: Internal analyzer error: {ex.Message}", 1, "internal");
            }

            Diagnostics = diags;
            AnalyzedAt = DateTime.UtcNow;
        }

        private static void Add(List<DocDiagnostic> diags, string message, int severity, string code)
        {
            var diag = new DocDiagnostic
            {
                Severity = severity,
                Code = code,
                Message = message
            };

            var m = LinePrefix.Match(message ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int oneBased))
            {
                int line = Math.Max(0, oneBased - 1);
                diag.Line = line;
                diag.Character = 0;
                diag.EndLine = line;
                diag.EndCharacter = 500;   // clients clamp to the real line end
                diag.Message = m.Groups[2].Value;
            }
            else
            {
                diag.Line = 0; diag.Character = 0;
                diag.EndLine = 0; diag.EndCharacter = 0;
                diag.Message = message;
            }
            diags.Add(diag);
        }

        public JsonArray ToDiagnosticJson()
        {
            var arr = new JsonArray();
            foreach (var d in Diagnostics)
            {
                arr.Add(new JsonObject
                {
                    ["range"] = LspJson.Range(d.Line, d.Character, d.EndLine, d.EndCharacter),
                    ["severity"] = d.Severity,
                    ["code"] = d.Code,
                    ["source"] = "nexus",
                    ["message"] = d.Message
                });
            }
            return arr;
        }
    }
}

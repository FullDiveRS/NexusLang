using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Nexus.Core;

namespace Nexus.Lsp
{
    /// <summary>Hover: keyword/builtin/asm docs plus user symbol signatures.</summary>
    public static class HoverEngine
    {
        public static JsonObject Hover(NexusWorkspace ws, NexusDocument doc, int line, int character)
        {
            int offset = LspText.OffsetAt(doc.Text, line, character);
            int idx = LspText.TokenIndexAt(doc.Tokens, offset);
            if (idx < 0) return null;

            var token = doc.Tokens[idx];

            // only hover when the caret actually touches the token
            int tokenEnd = token.Position + LspText.RawLength(token);
            if (offset > tokenEnd && token.Type != TokenType.Identifier) return null;

            string markdown = MarkdownFor(ws, doc, token, offset);
            if (markdown == null) return null;

            var (sl, sc) = LspText.PositionAt(doc.Text, token.Position);
            var (el, ec) = LspText.PositionAt(doc.Text, Math.Min(doc.Text.Length, token.Position + LspText.RawLength(token)));
            return new JsonObject
            {
                ["contents"] = LspJson.Markdown(markdown),
                ["range"] = LspJson.Range(sl, sc, el, ec)
            };
        }

        private static string MarkdownFor(NexusWorkspace ws, NexusDocument doc, Token token, int offset)
        {
            string word = token.Value ?? "";

            switch (token.Type)
            {
                case TokenType.Identifier:
                {
                    // asm context: mnemonic or register?
                    if (LspText.IsInsideAsm(doc.Tokens, offset))
                    {
                        var upper = word.ToUpperInvariant();
                        if (NexusDocs.AsmMnemonics.TryGetValue(upper, out var mn))
                            return $"**`{mn.form}`** (NASM)\n\n{mn.desc}";
                        if (NexusDocs.RegisterDocs.TryGetValue(word.ToLowerInvariant(), out var rd))
                            return $"**`{word.ToLowerInvariant()}`** (register)\n\n{rd}";
                        if (NexusDocs.AsmDirectives.TryGetValue(word.ToLowerInvariant(), out var dd))
                            return $"**`{dd.form}`** (directive)\n\n{dd.desc}";
                    }

                    if (NexusDocs.Builtins.TryGetValue(word, out var b))
                        return $"**`{b.sig}`** — builtin\n\n{b.desc}";

                    try { ws.EnsureIndexed(); } catch { }
                    var sym = ws.Lookup(word);
                    if (sym != null)
                    {
                        var where = sym.Uri == doc.Uri ? "this document" : System.IO.Path.GetFileName(sym.Uri);
                        var kindLabel = sym.Kind switch
                        {
                            "function" => "Function",
                            "method" => $"Method{(sym.Container != null ? $" of {sym.Container}" : "")}",
                            "class" => "Class",
                            "struct" => "Struct",
                            "enum" => "Enum",
                            "enumMember" => $"Enum constant{(sym.Container != null ? $" of {sym.Container}" : "")}",
                            "property" => $"Property{(sym.Container != null ? $" of {sym.Container}" : "")}",
                            "field" => $"Field{(sym.Container != null ? $" of {sym.Container}" : "")}",
                            "var" => "Variable",
                            "constant" => "Constant",
                            "event" => $"Event{(sym.Container != null ? $" of {sym.Container}" : "")}",
                            "delegate" => "Delegate type",
                            "operator" => $"Operator overload{(sym.Container != null ? $" of {sym.Container}" : "")}",
                            "asmModule" => "ASM module",
                            _ => sym.Kind
                        };
                        var stat = sym.IsStatic ? " **static**" : "";
                        return $"**{kindLabel}**{stat}: `{sym.Detail}`\n\nDeclared at line {sym.Line} in *{where}*.";
                    }
                    return null;
                }

                case TokenType.Print:
                    return $"**`print expr, ...;`** — statement\n\n{NexusDocs.KeywordDocs["print"]}";
                case TokenType.Println:
                    return $"**`println expr, ...;`** — statement\n\n{NexusDocs.KeywordDocs["println"]}";

                default:
                {
                    string lower = word.ToLowerInvariant();
                    if (NexusDocs.KeywordDocs.TryGetValue(lower, out var kd))
                        return $"**`{lower}`** — keyword\n\n{kd}";
                    if (NexusDocs.RegisterDocs.TryGetValue(lower, out var reg))
                        return $"**`{lower}`** (register)\n\n{reg}";
                    return null;
                }
            }
        }
    }
}

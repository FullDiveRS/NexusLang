using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nexus.Core;

namespace Nexus.Lsp
{
    /// <summary>Document symbols (outline + Ctrl+Shift+O) and go-to-definition.</summary>
    public static class SymbolEngine
    {
        // LSP SymbolKind values
        const int SKModule = 2, SKNamespace = 3, SKClass = 5, SKMethod = 6,
                  SKProperty = 7, SKField = 8, SKEnum = 10, SKInterface = 11, SKFunction = 12,
                  SKVariable = 13, SKConstant = 14, SKEnumMember = 22, SKStruct = 23,
                  SKEvent = 24, SKOperator = 25;

        // ============ document symbols ============
        private class Entry
        {
            public string Name;
            public string Detail;
            public int Kind;
            public int Start;          // offset of the declaration
            public int NameLength;
            public int ComputedEnd;    // assigned by AssignRanges
            public List<Entry> Children = new();
        }

        public static JsonArray DocumentSymbols(NexusDocument doc)
        {
            string text = doc.Text ?? "";
            _currentText = text;               // used by OffsetOf during collection
            var roots = new List<Entry>();
            Collect(doc.Program?.Statements, roots);
            roots.Sort((a, b) => a.Start.CompareTo(b.Start));
            AssignRanges(roots, 0, Math.Max(1, text.Length));

            var arr = new JsonArray();
            foreach (var e in roots)
                arr.Add(ToJson(e, text));
            return arr;
        }

        private static void Collect(List<Statement> statements, List<Entry> into)
        {
            if (statements == null) return;
            foreach (var stmt in statements)
                CollectOne(stmt, into);
        }

        private static Entry MakeEntry(string name, string detail, int kind, AstNode node)
        {
            return new Entry
            {
                Name = name,
                Detail = detail,
                Kind = kind,
                Start = OffsetOf(node?.Line ?? 1, node?.Column ?? 1),
                NameLength = Math.Max(1, name?.Length ?? 1)
            };
        }

        private static void CollectOne(Statement stmt, List<Entry> into)
        {
            switch (stmt)
            {
                case FunctionDecl f:
                {
                    var name = f.IsOperator ? $"operator {f.OperatorSymbol}" : f.Name;
                    var e = MakeEntry(name,
                        f.IsOperator
                            ? $"operator {f.OperatorSymbol}({Params(f.Parameters)})"
                            : $"func {f.Name}({Params(f.Parameters)})",
                        f.IsOperator ? SKOperator : SKMethod, f);
                    Collect(f.Body, e.Children);
                    into.Add(e);
                    break;
                }
                case ClassDecl c:
                {
                    var e = MakeEntry(c.Name,
                        $"class {c.Name}" + (c.BaseClass != null ? $" : {c.BaseClass}" : ""),
                        SKClass, c);
                    Collect(c.Members, e.Children);
                    into.Add(e);
                    break;
                }
                case EnumDecl en:
                {
                    var e = MakeEntry(en.Name, $"enum {en.Name}", SKEnum, en);
                    foreach (var cst in en.Members)
                        e.Children.Add(MakeEntry(cst.Item1,
                            $"{en.Name}.{cst.Item1} = {cst.Item2}", SKEnumMember, en));
                    Collect(en.MemberStatements, e.Children);
                    into.Add(e);
                    break;
                }
                case StructDecl s:
                    into.Add(MakeEntry(s.Name, $"struct {s.Name}", SKStruct, s));
                    break;
                case DelegateDecl d:
                    into.Add(MakeEntry(d.Name,
                        $"delegate {d.ReturnType ?? "void"} {d.Name}({Params(d.Parameters)})",
                        SKInterface, d));
                    break;
                case EventDecl ev:
                    into.Add(MakeEntry(ev.Name, $"event {ev.DelegateType} {ev.Name}", SKEvent, ev));
                    break;
                case PropertyDecl p:
                    into.Add(MakeEntry(p.Name,
                        $"var {p.Name} {{ {(p.HasGetter ? "get; " : "")}{(p.HasSetter ? "set; " : "")}}}",
                        SKProperty, p));
                    break;
                case VariableDecl v:
                    into.Add(MakeEntry(v.Name,
                        $"{v.Modifier} {v.Name}" + (v.Initializer is AsmModuleExpr ? " (asm module)" : ""),
                        v.Initializer is AsmModuleExpr ? SKModule
                            : v.Modifier == "const" ? SKConstant : SKVariable, v));
                    break;
                case MultiVariableDecl mv:
                    foreach (var d in mv.Declarations)
                        into.Add(MakeEntry(d.Name, $"{d.Modifier} {d.Name}",
                            d.Modifier == "const" ? SKConstant : SKVariable, d));
                    break;
                case NamespaceDecl ns:
                {
                    var e = MakeEntry(ns.Name, $"namespace {ns.Name}", SKNamespace, ns);
                    Collect(ns.Body, e.Children);
                    into.Add(e);
                    break;
                }
            }
        }

        private static string Params(List<Parameter> ps)
        {
            var parts = new List<string>();
            foreach (var p in ps ?? new List<Parameter>())
                parts.Add(p.Type != null ? $"{p.Name}: {p.Type}" : p.Name);
            return string.Join(", ", parts);
        }

        // AST nodes only carry (line, column) in 1-based lexer coordinates;
        // convert through LspText using the CURRENT document text.
        private static int OffsetOf(int oneBasedLine, int oneBasedColumn)
        {
            return LspText.OffsetAt(_currentText ?? "",
                Math.Max(0, oneBasedLine - 1), Math.Max(0, oneBasedColumn - 1));
        }

        [ThreadStatic] private static string _currentText;

        /// <summary>Assign each entry a range [start, nextStart) so the outline nests
        /// correctly; the selection range is the name span.</summary>
        private static void AssignRanges(List<Entry> entries, int minStart, int maxEnd)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Start < minStart) e.Start = minStart;
                int end = (i + 1 < entries.Count)
                    ? Math.Max(e.Start + 1, entries[i + 1].Start)
                    : Math.Max(e.Start + 1, maxEnd);
                e.ComputedEnd = end;
                if (e.Children.Count > 0)
                {
                    e.Children.Sort((a, b) => a.Start.CompareTo(b.Start));
                    AssignRanges(e.Children, e.Start, end);
                }
            }
        }

        private static JsonObject ToJson(Entry e, string text)
        {
            var (sl, sc) = LspText.PositionAt(text, e.Start);
            var (el, ec) = LspText.PositionAt(text, Math.Min(text.Length, e.ComputedEnd));

            int nameStart = FindNameOffset(text, e.Start, e.Name);
            var (nl, nc) = LspText.PositionAt(text, nameStart);

            var obj = new JsonObject
            {
                ["name"] = e.Name,
                ["detail"] = e.Detail,
                ["kind"] = e.Kind,
                ["range"] = LspJson.Range(sl, sc, el, ec),
                ["selectionRange"] = LspJson.Range(nl, nc, nl, nc + e.NameLength)
            };
            if (e.Children.Count > 0)
            {
                var arr = new JsonArray();
                foreach (var c in e.Children)
                    arr.Add(ToJson(c, text));
                obj["children"] = arr;
            }
            return obj;
        }

        /// <summary>Scan forward from the declaration start for the actual name word
        /// (skips `func`/`class`/modifiers); falls back to the declaration start.</summary>
        private static int FindNameOffset(string text, int start, string name)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(text)) return start;
            int searchEnd = Math.Min(text.Length, start + 400);
            int i = start;
            while (i < searchEnd)
            {
                if (char.IsLetter(text[i]) || text[i] == '_')
                {
                    int wordStart = i;
                    while (i < searchEnd && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    if (string.Equals(text.Substring(wordStart, i - wordStart), name, StringComparison.Ordinal))
                        return wordStart;
                }
                else i++;
            }
            return start;
        }

        // ============ go-to-definition ============
        public static JsonArray Definitions(NexusWorkspace ws, NexusDocument doc, int line, int character)
        {
            int offset = LspText.OffsetAt(doc.Text, line, character);
            string word = LspText.WordAt(doc.Text, offset);
            if (string.IsNullOrEmpty(word)) return new JsonArray();

            // `Class.member` — also try the member name alone
            string[] candidates = { word };
            int dot = word.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < word.Length)
                candidates = new[] { word.Substring(dot + 1), word };

            try { ws.EnsureIndexed(); } catch { }

            foreach (var candidate in candidates)
            {
                var sym = ws.Lookup(candidate);
                if (sym == null) continue;

                var text = TextOf(ws, sym.Uri);
                int nameStart = FindNameOffset(text,
                    LspText.OffsetAt(text, Math.Max(0, sym.Line - 1), Math.Max(0, sym.Column - 1)), sym.Name);
                var (sl, sc) = LspText.PositionAt(text, nameStart);

                return new JsonArray
                {
                    LspJson.Location(sym.Uri, sl, sc, sl, sc + Math.Max(1, sym.Name.Length))
                };
            }
            return new JsonArray();
        }

        private static string TextOf(NexusWorkspace ws, string uri)
        {
            var d = ws.Get(uri);
            if (d != null) return d.Text;
            try
            {
                if (uri.StartsWith("file://"))
                    return System.IO.File.ReadAllText(new Uri(uri).LocalPath);
            }
            catch { }
            return "";
        }
    }
}

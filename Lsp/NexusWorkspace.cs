using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Core;

namespace Nexus.Lsp
{
    /// <summary>A named symbol found in a parsed document.</summary>
    public class SymbolEntry
    {
        public string Name;
        public string Kind;          // function / method / class / enum / enumMember / var / field / property / event / delegate / operator / asmModule / struct / constant
        public string Detail;
        public string Container;     // enclosing class/enum name, if a member
        public string Uri;
        public int Line, Column;     // 1-based (lexer coordinates)
        public bool IsStatic;

        public override string ToString() => $"{Kind} {Name} @ {Path.GetFileName(Uri)}:{Line}";
    }

    /// <summary>Holds open documents, indexes their symbols for definition lookup,
    /// and lazily indexes .nx files under the workspace root.</summary>
    public class NexusWorkspace
    {
        public string RootPath { get; set; }
        public Dictionary<string, NexusDocument> Documents { get; } = new(StringComparer.Ordinal);
        private Dictionary<string, List<SymbolEntry>> _fileIndex;   // uri → symbols (closed files)
        private DateTime _indexedAt = DateTime.MinValue;

        public void Open(NexusDocument doc) => Documents[doc.Uri] = doc;

        public void Close(string uri) => Documents.Remove(uri);

        public NexusDocument Get(string uri) =>
            Documents.TryGetValue(uri, out var d) ? d : null;

        /// <summary>All known symbol entries from open documents + indexed root files.</summary>
        public List<SymbolEntry> AllSymbols()
        {
            var result = new List<SymbolEntry>();
            foreach (var doc in Documents.Values)
                result.AddRange(CollectSymbols(doc.Uri, doc.Program));
            foreach (var list in (_fileIndex ?? new()).Values)
                result.AddRange(list);
            return result;
        }

        public SymbolEntry Lookup(string name)
        {
            // open documents first
            foreach (var doc in Documents.Values)
            {
                foreach (var s in CollectSymbols(doc.Uri, doc.Program))
                {
                    if (string.Equals(s.Name, name, StringComparison.Ordinal))
                        return s;
                }
            }
            // then indexed workspace files
            if (_fileIndex != null)
            {
                foreach (var list in _fileIndex.Values)
                    foreach (var s in list)
                        if (string.Equals(s.Name, name, StringComparison.Ordinal))
                            return s;
            }
            return null;
        }

        /// <summary>Walk a program AST collecting every named declaration.</summary>
        public static List<SymbolEntry> CollectSymbols(string uri, ProgramNode program)
        {
            var result = new List<SymbolEntry>();
            if (program == null) return result;
            Walk(program.Statements, uri, result);
            return result;
        }

        private static void Walk(List<Statement> statements, string uri, List<SymbolEntry> into, string container = null)
        {
            if (statements == null) return;
            foreach (var stmt in statements)
                WalkOne(stmt, uri, into, container);
        }

        private static void AddSym(List<SymbolEntry> into, string uri, string name, string kind,
                                   string detail, AstNode node, bool isStatic = false, string container = null)
        {
            if (string.IsNullOrEmpty(name)) return;
            into.Add(new SymbolEntry
            {
                Name = name, Kind = kind, Detail = detail, Uri = uri,
                Line = Math.Max(1, node?.Line ?? 1), Column = Math.Max(1, node?.Column ?? 1),
                IsStatic = isStatic, Container = container
            });
        }

        private static void WalkOne(Statement stmt, string uri, List<SymbolEntry> into, string container = null)
        {
            switch (stmt)
            {
                case FunctionDecl f:
                    if (f.IsOperator)
                        AddSym(into, uri, f.OperatorSymbol ?? "op", "operator",
                               $"operator {f.OperatorSymbol}({ParamsOf(f)})", f);
                    else
                        AddSym(into, uri, f.Name, container != null ? "method" : "function",
                               $"func {f.Name}({ParamsOf(f)})" + (f.ReturnType != null ? $" -> {f.ReturnType}" : ""),
                               f, f.IsStatic, container);
                    Walk(f.Body, uri, into);
                    break;

                case ClassDecl c:
                    AddSym(into, uri, c.Name, "class",
                           $"class {c.Name}" + (c.BaseClass != null ? $" : {c.BaseClass}" : ""), c);
                    foreach (var m in c.Members) WalkOne(m, uri, into, c.Name);
                    break;

                case EnumDecl e:
                    AddSym(into, uri, e.Name, "enum",
                           $"enum {e.Name} ({e.Members.Count} constants, {e.MemberStatements.Count} members)", e);
                    foreach (var cst in e.Members)
                        AddSym(into, uri, cst.Item1, "enumMember", $"{e.Name}.{cst.Item1} = {cst.Item2}", e, container: e.Name);
                    Walk(e.MemberStatements, uri, into, e.Name);
                    break;

                case StructDecl s:
                    AddSym(into, uri, s.Name, "struct", $"struct {s.Name}", s);
                    break;

                case DelegateDecl d:
                    AddSym(into, uri, d.Name, "delegate",
                           $"delegate {d.ReturnType ?? "void"} {d.Name}({ParamsOf(d.Name, d.Parameters)})", d);
                    break;

                case EventDecl ev:
                    AddSym(into, uri, ev.Name, "event",
                           $"event {ev.DelegateType} {ev.Name}", ev, ev.IsStatic, container);
                    break;

                case PropertyDecl p:
                    AddSym(into, uri, p.Name, "property",
                           $"var {p.Name} {{ {(p.HasGetter ? "get; " : "")}{(p.HasSetter ? "set; " : "")}}}", p, p.IsStatic, container);
                    break;

                case VariableDecl v:
                    AddSym(into, uri, v.Name,
                           v.Initializer is AsmModuleExpr ? "asmModule"
                               : container != null ? "field"
                               : v.Modifier == "const" ? "constant" : "var",
                           $"{v.Modifier} {v.Name}" + (v.Initializer is AsmModuleExpr ? " (asm module)" : ""),
                           v, v.IsStatic, container);
                    break;

                case MultiVariableDecl mv:
                    foreach (var d2 in mv.Declarations)
                        AddSym(into, uri, d2.Name,
                               container != null ? "field" : (d2.Modifier == "const" ? "constant" : "var"),
                               $"{d2.Modifier} {d2.Name}", d2, d2.IsStatic, container);
                    break;

                case NamespaceDecl ns:
                    AddSym(into, uri, ns.Name, "namespace", $"namespace {ns.Name}", ns);
                    Walk(ns.Body, uri, into);
                    break;
            }
        }

        private static string ParamsOf(FunctionDecl f) => ParamsOf(null, f.Parameters);

        private static string ParamsOf(string _, List<Parameter> ps)
        {
            var parts = new List<string>();
            foreach (var p in ps ?? new List<Parameter>())
                parts.Add(p.Type != null ? $"{p.Name}: {p.Type}" : p.Name);
            return string.Join(", ", parts);
        }

        /// <summary>Index .nx files under the workspace root (depth-capped, skips build dirs).</summary>
        public void EnsureIndexed()
        {
            if (string.IsNullOrEmpty(RootPath) || !Directory.Exists(RootPath)) return;
            if (_fileIndex != null && (DateTime.UtcNow - _indexedAt).TotalSeconds < 5) return;

            _fileIndex = new Dictionary<string, List<SymbolEntry>>(StringComparer.Ordinal);
            int budget = 300;
            var queue = new Queue<(string dir, int depth)>();
            queue.Enqueue((RootPath, 0));
            var seen = new HashSet<string>(StringComparer.Ordinal);

            while (queue.Count > 0 && budget > 0)
            {
                var (dir, depth) = queue.Dequeue();
                if (depth > 4) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*.nx"))
                    {
                        budget--;
                        var full = Path.GetFullPath(file);
                        if (!seen.Add(full)) continue;
                        try
                        {
                            var text = File.ReadAllText(full);
                            var lexer = new Lexer(text);
                            var tokens = lexer.Tokenize();
                            var parser = new Parser(tokens);
                            var program = parser.ParseProgram();
                            var uri = new Uri(full).AbsoluteUri;
                            _fileIndex[uri] = CollectSymbols(uri, program);
                        }
                        catch { /* unreadable file — skip */ }
                        if (budget <= 0) break;
                    }
                    if (depth < 4)
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir))
                        {
                            var name = Path.GetFileName(sub);
                            if (name == "bin" || name == "obj" || name == ".git" || name == "node_modules" ||
                                name == ".vscode" || name == ".vs" || name.StartsWith('.'))
                                continue;
                            queue.Enqueue((sub, depth + 1));
                        }
                    }
                }
                catch { /* unreadable dir — skip */ }
            }
            _indexedAt = DateTime.UtcNow;
        }
    }
}

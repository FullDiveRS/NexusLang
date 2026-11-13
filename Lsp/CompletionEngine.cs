using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Nexus.Core;

namespace Nexus.Lsp
{
    /// <summary>Context-aware completion: top-level keywords + snippets + builtins +
    /// user symbols normally; NASM mnemonics/registers inside asm blocks; typed
    /// members (labels, statics, instance fields) after `.`.</summary>
    public static class CompletionEngine
    {
        // completion item kinds (LSP CompletionItemKind)
        const int KMethod = 2, KFunction = 3, KField = 5, KVariable = 6, KClass = 7,
                  KModule = 9, KProperty = 10, KValue = 12, KEnum = 13, KKeyword = 14,
                  KSnippet = 15, KEnumMember = 20, KConstant = 21, KEvent = 23, KOperator = 24;

        public static JsonObject Complete(NexusWorkspace ws, NexusDocument doc, int line, int character)
        {
            int offset = LspText.OffsetAt(doc.Text, line, character);
            string prefix = LspText.WordBefore(doc.Text, offset);

            if (LspText.IsInComment(doc.Text, offset) || LspText.IsInString(doc.Tokens, offset))
                return List();

            var items = new List<JsonObject>();

            if (LspText.IsInsideAsm(doc.Tokens, offset))
            {
                AddAsmCompletions(doc, offset, items);
            }
            else
            {
                var receiver = MemberReceiver(doc, offset);
                if (receiver != null)
                    AddMemberCompletions(ws, doc, receiver, items);
                else
                    AddTopLevelCompletions(ws, doc, items);
            }

            return List(items);
        }

        private static JsonObject List(List<JsonObject> items = null) => new JsonObject
        {
            ["isIncomplete"] = false,
            ["items"] = items == null ? new JsonArray() : new JsonArray(items.ToArray())
        };

        // ============ receiver detection: what is before the cursor's `.`? ============
        private class ReceiverInfo
        {
            public string Name;
            public bool IsThis;
        }

        private static ReceiverInfo MemberReceiver(NexusDocument doc, int offset)
        {
            int idx = LspText.TokenIndexAt(doc.Tokens, offset);
            if (idx < 0) return null;

            var t = doc.Tokens[idx];

            // skip a terminator the cursor sits behind (e.g. `math.` + newline,
            // so completion still fires on the empty line after the dot)
            while ((t.Type == TokenType.Newline || t.Type == TokenType.Semicolon) && idx > 0)
                t = doc.Tokens[--idx];

            // Case 1: cursor right after the dot          → t == Dot
            // Case 2: cursor inside/at end of member word → t == Identifier preceded by Dot
            int dotIdx = -1;
            if (t.Type == TokenType.Dot)
                dotIdx = idx;
            else if (t.Type == TokenType.Identifier && idx >= 1 &&
                     doc.Tokens[idx - 1].Type == TokenType.Dot)
                dotIdx = idx - 1;
            if (dotIdx < 0) return null;

            int recIdx = dotIdx - 1;
            if (recIdx >= 0 && doc.Tokens[recIdx].Type == TokenType.Question)
                recIdx--;                                  // `?.` null-conditional
            if (recIdx < 0) return null;

            var rec = doc.Tokens[recIdx];
            if (rec.Type == TokenType.Identifier)
                return new ReceiverInfo { Name = rec.Value };
            if (rec.Type == TokenType.This)
                return new ReceiverInfo { Name = "this", IsThis = true };
            if (rec.Type == TokenType.Base)
                return new ReceiverInfo { Name = "this", IsThis = true };
            return null;
        }

        // ============ top-level: keywords, snippets, builtins, user symbols ============
        private static void AddTopLevelCompletions(NexusWorkspace ws, NexusDocument doc, List<JsonObject> items)
        {
            // user symbols first (functions/classes/enums/vars across the workspace)
            try { ws.EnsureIndexed(); } catch { }
            foreach (var s in ws.AllSymbols())
            {
                if (s.Kind == "operator" || s.Kind == "namespace") continue;
                items.Add(Item(
                    label: s.Name,
                    kind: s.Kind switch
                    {
                        "function" => KFunction,
                        "method" => KMethod,
                        "class" => KClass,
                        "struct" => KClass,
                        "enum" => KEnum,
                        "enumMember" => KEnumMember,
                        "field" => KField,
                        "property" => KProperty,
                        "event" => KEvent,
                        "delegate" => KFunction,
                        "constant" => KConstant,
                        "asmModule" => KModule,
                        _ => KVariable
                    },
                    detail: s.Detail,
                    doc: $"{s.Kind} — defined at line {s.Line}",
                    sortBoost: 2));
            }

            // builtin functions
            foreach (var kv in NexusDocs.Builtins)
            {
                items.Add(Item(kv.Key, KFunction, kv.Value.sig, kv.Value.desc, sortBoost: 3));
            }

            // type keywords
            foreach (var tk in NexusDocs.TypeKeywords)
            {
                var d = NexusDocs.KeywordDocs.TryGetValue(tk, out var dd) ? dd : $"Type keyword `{tk}`.";
                items.Add(Item(tk, KKeyword, tk, d, sortBoost: 4));
            }

            // declaration/statement keywords (skip types, already added)
            foreach (var kv in NexusDocs.KeywordDocs)
            {
                if (NexusDocs.TypeKeywords.Contains(kv.Key)) continue;
                items.Add(Item(kv.Key, KKeyword, kv.Key, kv.Value, sortBoost: 5));
            }

            // structured snippets
            foreach (var s in NexusDocs.Snippets)
            {
                items.Add(new JsonObject
                {
                    ["label"] = s.label,
                    ["kind"] = KSnippet,
                    ["detail"] = s.detail,
                    ["documentation"] = LspJson.Markdown($"```\n{s.snippet}\n```"),
                    ["insertText"] = s.snippet,
                    ["insertTextFormat"] = 2,
                    ["sortText"] = $"1{s.label}"
                });
            }
        }

        // ============ member completion after `.` ============
        private static void AddMemberCompletions(NexusWorkspace ws, NexusDocument doc, ReceiverInfo receiver, List<JsonObject> items)
        {
            bool any = false;

            foreach (var s in ws.AllSymbols())
            {
                if (!string.Equals(s.Name, receiver.Name, StringComparison.Ordinal)) continue;

                switch (s.Kind)
                {
                    case "asmModule":
                        any = true;
                        AddAsmModuleMembers(doc, s.Name, items);
                        break;

                    case "class":
                    case "struct":
                        any = true;
                        AddClassMembers(doc, s.Name, staticsOnly: true, items);
                        break;

                    case "enum":
                        any = true;
                        AddEnumMembers(doc, s.Name, items);
                        break;
                }
            }

            if (!any)
            {
                // unknown receiver — maybe a var typed via `new ClassName(...)`, or this/super
                var className = InferVarType(doc, receiver.Name);
                if (className != null)
                {
                    any = true;
                    AddClassMembers(doc, className, staticsOnly: false, items);
                }
                else if (receiver.IsThis)
                {
                    any = true;
                    AddClassMembers(doc, null, staticsOnly: false, items);
                }
            }

            if (!any)
            {
                // graceful fallback: everything user-defined + builtins
                AddTopLevelCompletions(ws, doc, items);
            }
        }

        /// <summary>Find `var name = new ClassName(...)` and return ClassName.</summary>
        private static string InferVarType(NexusDocument doc, string varName)
        {
            if (doc.Program == null) return null;
            return FindVarType(doc.Program.Statements, varName);
        }

        private static string FindVarType(List<Statement> statements, string varName)
        {
            if (statements == null) return null;
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case VariableDecl v when v.Name == varName && v.Initializer is NewExpr ne:
                        return ne.TypeName;
                    case MultiVariableDecl mv:
                        foreach (var d in mv.Declarations)
                            if (d.Name == varName && d.Initializer is NewExpr ne2)
                                return ne2.TypeName;
                        break;
                    case ClassDecl c:
                        foreach (var m in c.Members)
                            if (m is VariableDecl cv && cv.Name == varName && cv.Initializer is NewExpr cne)
                                return cne.TypeName;
                        break;
                    case BlockStmt b:
                        var r = FindVarType(b.Statements, varName);
                        if (r != null) return r;
                        break;
                    case FunctionDecl f:
                        var rf = FindVarType(f.Body, varName);
                        if (rf != null) return rf;
                        break;
                }
            }
            return null;
        }

        private static void AddAsmModuleMembers(NexusDocument doc, string moduleName, List<JsonObject> items)
        {
            var block = FindAsmBlock(doc.Program?.Statements, moduleName);
            if (block != null)
            {
                var dataNames = new HashSet<string>(block.DataDefinitions.Select(d => d.Name));
                foreach (var label in block.Labels)
                {
                    if (string.IsNullOrEmpty(label)) continue;
                    bool isData = dataNames.Contains(label);
                    items.Add(Item(label, isData ? KVariable : KFunction,
                        isData ? $"{moduleName}.{label} (data)" : $"{moduleName}.{label}()",
                        isData ? $"Data label — read/write: `{moduleName}.{label}`, raw address: `{moduleName}.{label}`"
                               : $"Callable label — `{moduleName}.{label}()`, result in EAX"));
                }
            }
            items.Add(Item("labels", KProperty, $"{moduleName}.labels", "List of all label names in this asm module."));
            items.Add(Item("data", KProperty, $"{moduleName}.data", "List of all data (db/dw/dd/dq) label names."));
        }

        private static AsmBlock FindAsmBlock(List<Statement> statements, string moduleName)
        {
            if (statements == null) return null;
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case VariableDecl v when v.Name == moduleName && v.Initializer is AsmModuleExpr am:
                        return am.Block;
                    case ClassDecl c:
                        foreach (var m in c.Members)
                            if (m is VariableDecl cv && cv.Name == moduleName && cv.Initializer is AsmModuleExpr cam)
                                return cam.Block;
                        break;
                    case BlockStmt b:
                        var r = FindAsmBlock(b.Statements, moduleName);
                        if (r != null) return r;
                        break;
                    case FunctionDecl f:
                        var rf = FindAsmBlock(f.Body, moduleName);
                        if (rf != null) return rf;
                        break;
                }
            }
            return null;
        }

        private static void AddClassMembers(NexusDocument doc, string className, bool staticsOnly, List<JsonObject> items)
        {
            var classes = new List<ClassDecl>();
            CollectClasses(doc.Program?.Statements, classes);
            var targets = className == null
                ? classes
                : classes.Where(c => string.Equals(c.Name, className, StringComparison.Ordinal)).ToList();

            foreach (var c in targets)
            {
                foreach (var m in c.Members)
                {
                    switch (m)
                    {
                        case FunctionDecl f when !f.IsOperator && !string.IsNullOrEmpty(f.Name):
                            if (staticsOnly && !f.IsStatic) continue;
                            items.Add(Item(f.Name, KMethod,
                                $"func {f.Name}({ParamsOf(f)})",
                                $"{(f.IsStatic ? "static " : "")}method of {c.Name}"));
                            break;
                        case FunctionDecl f2 when f2.IsOperator:
                            if (staticsOnly) continue;
                            items.Add(Item(f2.OperatorSymbol ?? "op", KOperator,
                                $"operator {f2.OperatorSymbol}({ParamsOf(f2)})",
                                $"Operator overload of {c.Name}"));
                            break;
                        case PropertyDecl p:
                            if (staticsOnly && !p.IsStatic) continue;
                            items.Add(Item(p.Name, KProperty,
                                $"var {p.Name} {{ {(p.HasGetter ? "get; " : "")}{(p.HasSetter ? "set; " : "")}}}",
                                $"{(p.IsStatic ? "static " : "")}property of {c.Name}"));
                            break;
                        case VariableDecl v:
                            if (staticsOnly && !v.IsStatic) continue;
                            items.Add(Item(v.Name, v.Modifier == "const" ? KConstant : KField,
                                $"{v.Modifier} {v.Name}",
                                $"{(v.IsStatic ? "static " : "")}field of {c.Name}"));
                            break;
                        case MultiVariableDecl mv:
                            foreach (var d in mv.Declarations)
                            {
                                if (staticsOnly && !d.IsStatic) continue;
                                items.Add(Item(d.Name, d.Modifier == "const" ? KConstant : KField,
                                    $"{d.Modifier} {d.Name}", $"{(d.IsStatic ? "static " : "")}field of {c.Name}"));
                            }
                            break;
                        case EventDecl ev:
                            if (staticsOnly && !ev.IsStatic) continue;
                            items.Add(Item(ev.Name, KEvent, $"event {ev.DelegateType} {ev.Name}",
                                $"{(ev.IsStatic ? "static " : "")}event of {c.Name} — add handlers with `+=`"));
                            break;
                        case DelegateDecl dd:
                            items.Add(Item(dd.Name, KFunction, $"delegate {dd.ReturnType ?? "void"} {dd.Name}", $"Delegate type in {c.Name}"));
                            break;
                    }
                }
            }
        }

        private static void CollectClasses(List<Statement> statements, List<ClassDecl> into)
        {
            if (statements == null) return;
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case ClassDecl c:
                        into.Add(c);
                        break;
                    case NamespaceDecl ns:
                        CollectClasses(ns.Body, into);
                        break;
                    case BlockStmt b:
                        CollectClasses(b.Statements, into);
                        break;
                }
            }
        }

        private static void AddEnumMembers(NexusDocument doc, string enumName, List<JsonObject> items)
        {
            var enums = new List<EnumDecl>();
            CollectEnums(doc.Program?.Statements, enums);
            foreach (var e in enums.Where(e => string.Equals(e.Name, enumName, StringComparison.Ordinal)))
            {
                foreach (var cst in e.Members)
                    items.Add(Item(cst.Item1, KEnumMember, $"{e.Name}.{cst.Item1} = {cst.Item2}",
                        $"Enum constant of {e.Name}"));
                foreach (var m in e.MemberStatements)
                {
                    switch (m)
                    {
                        case FunctionDecl f:
                            items.Add(Item(f.Name, KMethod, $"func {f.Name}({ParamsOf(f)})",
                                $"Static method of enum {e.Name}"));
                            break;
                        case VariableDecl v:
                            items.Add(Item(v.Name, KField, $"{v.Modifier} {v.Name}",
                                $"Static field of enum {e.Name}"));
                            break;
                        case PropertyDecl p:
                            items.Add(Item(p.Name, KProperty, $"var {p.Name}", $"Property of enum {e.Name}"));
                            break;
                        case EventDecl ev:
                            items.Add(Item(ev.Name, KEvent, $"event {ev.DelegateType} {ev.Name}", $"Event of enum {e.Name}"));
                            break;
                    }
                }
            }
        }

        private static void CollectEnums(List<Statement> statements, List<EnumDecl> into)
        {
            if (statements == null) return;
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case EnumDecl e: into.Add(e); break;
                    case NamespaceDecl ns: CollectEnums(ns.Body, into); break;
                    case BlockStmt b: CollectEnums(b.Statements, into); break;
                }
            }
        }

        // ============ asm-block completion ============
        private static void AddAsmCompletions(NexusDocument doc, int offset, List<JsonObject> items)
        {
            foreach (var kv in NexusDocs.AsmMnemonics)
                items.Add(Item(kv.Key, KFunction, kv.Value.form, kv.Value.desc, sortBoost: 0, lower: true));

            foreach (var reg in NexusDocs.AsmRegisters)
            {
                var d = NexusDocs.RegisterDocs.TryGetValue(reg, out var rd) ? rd : "Register.";
                items.Add(Item(reg, KValue, reg, d, sortBoost: 1, lower: true));
            }

            foreach (var kv in NexusDocs.AsmDirectives)
                items.Add(Item(kv.Key, KKeyword, kv.Value.form, kv.Value.desc, sortBoost: 1, lower: true));

            // Nexus variables — writable asm targets (`mov total, eax`)
            CollectVarNames(doc.Program?.Statements, new HashSet<string>(), items);
        }

        private static void CollectVarNames(List<Statement> statements, HashSet<string> seen, List<JsonObject> items)
        {
            if (statements == null) return;
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case VariableDecl v when !seen.Contains(v.Name):
                        seen.Add(v.Name);
                        items.Add(Item(v.Name, KVariable, $"{v.Modifier} {v.Name}",
                            "Nexus variable — usable as an asm destination: `mov " + v.Name + ", eax`", sortBoost: 2));
                        break;
                    case MultiVariableDecl mv:
                        foreach (var d in mv.Declarations)
                            if (!seen.Contains(d.Name))
                            {
                                seen.Add(d.Name);
                                items.Add(Item(d.Name, KVariable, $"{d.Modifier} {d.Name}",
                                    "Nexus variable — usable as an asm destination", sortBoost: 2));
                            }
                        break;
                    case FunctionDecl f:
                        CollectVarNames(f.Body, seen, items);
                        break;
                    case ClassDecl c:
                        var sub = new HashSet<string>(seen);
                        foreach (var m in c.Members)
                            if (m is VariableDecl cm) CollectVarNames(new List<Statement> { cm }, sub, items);
                        break;
                }
            }
        }

        // ============ item factory ============
        private static string ParamsOf(FunctionDecl f)
        {
            var parts = new List<string>();
            foreach (var p in f.Parameters)
                parts.Add(p.Type != null ? $"{p.Name}: {p.Type}" : p.Name);
            return string.Join(", ", parts);
        }

        private static JsonObject Item(string label, int kind, string detail, string doc,
                                       int sortBoost = 0, bool lower = false)
        {
            var obj = new JsonObject
            {
                ["label"] = label,
                ["kind"] = kind,
                ["detail"] = detail ?? label,
                ["sortText"] = $"{(char)('0' + Math.Min(sortBoost, 9))}{(lower ? label.ToLowerInvariant() : label)}"
            };
            if (!string.IsNullOrEmpty(doc))
                obj["documentation"] = LspJson.Markdown(doc.StartsWith("```") ? doc : $"```nexus\n{label}\n```\n{doc}");
            return obj;
        }
    }
}

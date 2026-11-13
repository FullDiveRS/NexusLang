using System;
using System.IO;
using System.Linq;
using Nexus.Bytecode;
using Nexus.Compiler;
using Nexus.Core;
using Nexus.Interop;
using Nexus.REPL;
using Nexus.Runtime;

namespace Nexus
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { /* console may not support UTF-8 — ignore */ }

            if (args.Length == 0)
            {
                new Repl().Run();
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "run" or "r" when args.Length > 1:
                    RunFile(args[1]);
                    break;
                case "repl" or "i":
                    new Repl().Run();
                    break;
                case "lsp" or "language-server":
                    RunLsp(args);
                    break;
                case "compile" or "c" when args.Length > 1:
                    CompileFile(args[1], args.Length > 2 ? args[2] : "output");
                    break;
                case "aot" when args.Length > 1:
                    GenerateAot(args[1], args.Length > 2 ? args[2] : "NexusOutput.cs");
                    break;
                case "bytecode" or "bc" when args.Length > 1:
                    ShowBytecode(args[1]);
                    break;
                case "exec" or "x" when args.Length > 1:
                    ExecBytecode(args[1]);
                    break;
                case "tokens" or "t" when args.Length > 1:
                    ShowTokens(args[1]);
                    break;
                case "ast" when args.Length > 1:
                    ShowAst(args[1]);
                    break;
                case "doctor" when args.Length > 1:
                    Doctor(args[1]);
                    break;
                case "interop":
                    Console.WriteLine(new InteropManager().GetInteropInfo());
                    break;
                case "version" or "v":
                    Console.WriteLine("Nexus v1.2.0 - C#-like language with NASM features + local language server");
                    break;
                default:
                    PrintHelp();
                    break;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"
NEXUS v1.2.0 — Usage: nexus <command> [args]

  run <file.nx>          Execute a source file
  repl                   Interactive REPL
  lsp                    Run the Language Server (stdio JSON-RPC, used by the VSCode extension)
  doctor <file.nx>       Diagnose the full pipeline (START HERE when nothing works)
  exec <file.nx|.nbc>    Compile-and-run source, or run saved .nbc bytecode
  compile <file> [out]   Compile to .nbc bytecode package + disassembly
  aot <file.nx> [out.cs] Transpile to compilable C#
  bytecode <file.nx>     Show bytecode disassembly
  tokens <file.nx>       Show lexer tokens
  ast <file.nx>          Show abstract syntax tree
  interop                Show interop system info
  version | help");
        }

        // ===== shared: parse a file, reporting EVERY error =====
        private static Tuple<Lexer, Parser, ProgramNode> ParseFile(string path)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Error: file not found: {path}");
                return null;
            }

            var lexer = new Lexer(File.ReadAllText(path));
            var tokens = lexer.Tokenize();
            if (!ReportErrors("Lexer", lexer.Errors)) return null;

            var parser = new Parser(tokens);
            var program = parser.ParseProgram();
            if (!ReportErrors("Parser", parser.Errors)) return null;

            if (program.Statements.Count == 0)
            {
                Console.WriteLine("(file parsed to 0 statements — nothing to do)");
                return null;
            }
            return Tuple.Create(lexer, parser, program);
        }

        private static bool ReportErrors(string stage, System.Collections.Generic.List<string> errors)
        {
            if (errors.Count == 0) return true;
            Console.Error.WriteLine($"{stage} errors ({errors.Count}):");
            foreach (var e in errors)
                Console.Error.WriteLine($"  {e}");
            return false;
        }

        // ===== run =====
        private static void RunFile(string path)
        {
            var parsed = ParseFile(path);
            if (parsed == null) Environment.Exit(1);

            try
            {
                new Interpreter(new InteropManager()).Interpret(parsed.Item3);
            }
            catch (NexusRuntimeException ex)
            {
                Console.Error.WriteLine($"Runtime error: {ex.Message}");
                Environment.Exit(1);
            }
        }

        // ===== lsp: language server over stdio =====
        private static void RunLsp(string[] args)
        {
            string logPath = null;
            foreach (var a in args)
            {
                if (a.StartsWith("--log=", StringComparison.Ordinal))
                    logPath = a.Substring(6);
                else if (a == "--log" && Array.IndexOf(args, a) + 1 < args.Length)
                    logPath = args[Array.IndexOf(args, a) + 1];
            }
            new Nexus.Lsp.LspServer().Run(Console.OpenStandardInput(), Console.OpenStandardOutput(), logPath);
        }

        // ===== doctor — runs every stage and shows everything =====
        private static void Doctor(string path)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"File not found: {path}");
                return;
            }
            var source = File.ReadAllText(path);
            bool allOk = true;

            Console.WriteLine("=== NEXUS DOCTOR ===");
            Console.WriteLine($"-- Source: {source.Length} chars --");

            var lexer = new Lexer(source);
            var tokens = lexer.Tokenize();
            Console.WriteLine("-- Lexer --");
            Console.WriteLine($"  Tokens: {tokens.Count}   Errors: {lexer.Errors.Count}");
            foreach (var e in lexer.Errors)
            {
                allOk = false;
                Console.WriteLine($"    ! {e}");
            }

            var parser = new Parser(tokens);
            var program = parser.ParseProgram();
            Console.WriteLine("-- Parser --");
            Console.WriteLine($"  Statements: {program.Statements.Count}   Errors: {parser.Errors.Count}");
            foreach (var e in parser.Errors)
            {
                allOk = false;
                Console.WriteLine($"    ! {e}");
            }
            foreach (var s in program.Statements)
                Console.WriteLine($"    [{s.GetType().Name}] {s}");

            Console.WriteLine("-- Interpreter --");
            try
            {
                var interp = new Interpreter(new InteropManager());
                interp.Interpret(program);
                Console.WriteLine($"  OK — captured output ({interp.Output.Count} lines):");
                foreach (var line in interp.Output)
                    Console.WriteLine($"    | {line}");
                if (interp.Output.Count == 0 && program.Statements.Count > 0)
                    Console.WriteLine("    (no output produced — check statement list above)");
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.WriteLine($"  FAILED: {ex.Message}");
            }

            Console.WriteLine("-- Bytecode --");
            try
            {
                var bc = new BytecodeCompiler();
                var chunks = bc.Compile(program);
                Console.WriteLine($"  Chunks: {chunks.Count}   Total instructions: {chunks.Values.Sum(c => c.Instructions.Count)}");
                foreach (var e in bc.Errors)
                {
                    allOk = false;
                    Console.WriteLine($"    ! {e}");
                }
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.WriteLine($"  FAILED: {ex.Message}");
            }

            Console.WriteLine("-- AOT --");
            try
            {
                var aot = new AotCompiler();
                var cs = aot.CompileToCSharp(program);
                Console.WriteLine($"  Generated: {cs.Count(c => c == '\n') + 1} lines");
                foreach (var w in aot.Warnings)
                    Console.WriteLine($"    ~ {w}");
            }
            catch (Exception ex)
            {
                allOk = false;
                Console.WriteLine($"  FAILED: {ex.Message}");
            }

            Console.WriteLine();
            Console.WriteLine(allOk
                ? "  [OK] Pipeline healthy."
                : "  [!!] Problems found — see '!' lines above.");
        }

        // ===== bytecode: show + exec =====
        private static void ShowBytecode(string path)
        {
            var parsed = ParseFile(path);
            if (parsed == null) return;

            var compiler = new BytecodeCompiler();
            var chunks = compiler.Compile(parsed.Item3);

            if (compiler.Errors.Count > 0)
            {
                Console.WriteLine($"Bytecode compiler warnings ({compiler.Errors.Count}):");
                foreach (var e in compiler.Errors)
                    Console.WriteLine($"  {e}");
            }

            foreach (var chunk in chunks)
                Console.WriteLine(chunk.Value.Disassemble());
        }

        private static void ExecBytecode(string path)
        {
            // .nbc package: deserialize and run; anything else: compile-and-run
            var vm = new VirtualMachine(new InteropManager());
            try
            {
                if (File.Exists(path) && HasBytecodeMagic(path))
                {
                    var chunks = BytecodeChunk.LoadPackage(path);
                    vm.Execute(chunks);
                    return;
                }
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"Bytecode load error: {ex.Message}");
                Environment.Exit(1);
            }

            var parsed = ParseFile(path);
            if (parsed == null) return;

            var compiler = new BytecodeCompiler();
            var compiled = compiler.Compile(parsed.Item3);
            if (!ReportErrors("Bytecode compiler", compiler.Errors))
                Environment.Exit(1);

            try
            {
                vm.Execute(compiled);
            }
            catch (NexusRuntimeException ex)
            {
                Console.Error.WriteLine($"VM error: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private static bool HasBytecodeMagic(string path)
        {
            var header = new byte[9];
            using (var fs = File.OpenRead(path))
            {
                if (fs.Length < header.Length) return false;
                if (fs.Read(header, 0, header.Length) != header.Length) return false;
            }
            return System.Text.Encoding.UTF8.GetString(header) == "NEXUS_BC2";
        }

        // ===== aot =====
        private static void GenerateAot(string sourcePath, string outputPath)
        {
            var parsed = ParseFile(sourcePath);
            if (parsed == null) return;

            var aot = new AotCompiler();
            var csharp = aot.CompileToCSharp(parsed.Item3,
                Path.GetFileNameWithoutExtension(sourcePath).Replace("-", "_"));

            if (aot.Warnings.Count > 0)
            {
                Console.WriteLine($"AOT warnings ({aot.Warnings.Count}):");
                foreach (var w in aot.Warnings)
                    Console.WriteLine($"  {w}");
            }

            var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            File.WriteAllText(outputPath, csharp);
            Console.WriteLine($"AOT: {sourcePath} -> {outputPath} ({csharp.Count(c => c == '\n') + 1} lines)");
            Console.WriteLine("Run it: drop the .cs into a console project (no extra packages needed), then `dotnet run`.");
        }

        // ===== compile to .nbc =====
        private static void CompileFile(string sourcePath, string outputName)
        {
            var parsed = ParseFile(sourcePath);
            if (parsed == null) return;

            var compiler = new BytecodeCompiler();
            var chunks = compiler.Compile(parsed.Item3);
            if (!ReportErrors("Bytecode compiler", compiler.Errors)) return;

            BytecodeChunk.SavePackage(chunks, outputName + ".nbc");
            File.WriteAllText(outputName + ".nasm.txt",
                string.Join("\n\n", chunks.Select(c => c.Value.Disassemble())));

            Console.WriteLine($"Compiled: {sourcePath} -> {outputName}.nbc ({chunks.Count} chunks)");
            Console.WriteLine($"Disassembly: {outputName}.nasm.txt");
        }

        // ===== tokens / ast =====
        private static void ShowTokens(string path)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Error: file not found: {path}");
                return;
            }

            var tokens = new Lexer(File.ReadAllText(path)).Tokenize();

            Console.WriteLine($"{"Type",-20} {"Value",-30} Line  Col");
            Console.WriteLine(new string('-', 66));
            foreach (var t in tokens)
            {
                if (t.Type == TokenType.EOF) break;
                var v = t.Value.Length > 28 ? t.Value.Substring(0, 25) + "..." : t.Value;
                Console.WriteLine($"{t.Type,-20} {v,-30} {t.Line,-5} {t.Column,-5}");
            }
            Console.WriteLine($"Total tokens: {tokens.Count}");
        }

        private static void ShowAst(string path)
        {
            var parsed = ParseFile(path);
            if (parsed == null) return;

            Console.WriteLine($"AST ({parsed.Item3.Statements.Count} top-level statements):");
            PrintAstNodes(parsed.Item3.Statements, 1);
        }

        private static void PrintAstNodes(System.Collections.Generic.List<Statement> statements, int depth)
        {
            var indent = new string(' ', depth * 2);
            foreach (var stmt in statements)
            {
                Console.WriteLine($"{indent}{stmt.GetType().Name}: {stmt}");
                switch (stmt)
                {
                    case BlockStmt b:
                        PrintAstNodes(b.Statements, depth + 1);
                        break;
                    case NamespaceDecl ns:
                        PrintAstNodes(ns.Body, depth + 1);
                        break;
                    case FunctionDecl f:
                        PrintAstNodes(f.Body, depth + 1);
                        break;
                    case ClassDecl c:
                        foreach (var m in c.Members)
                        {
                            Console.WriteLine($"{indent}  {m.GetType().Name}: {m}");
                            if (m is FunctionDecl mf)
                                PrintAstNodes(mf.Body, depth + 2);
                        }
                        break;
                }
            }
        }
    }
}

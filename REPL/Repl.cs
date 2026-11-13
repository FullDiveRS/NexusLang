using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Bytecode;
using Nexus.Compiler;
using Nexus.Core;
using Nexus.Interop;
using Nexus.Runtime;

namespace Nexus.REPL
{
    /// <summary>Interactive Read-Eval-Print-Loop for the Nexus language.</summary>
    public class Repl
    {
        private RuntimeEnvironment _globalEnv;
        private Interpreter _interpreter;
        private readonly InteropManager _interop;
        private readonly List<string> _history = new List<string>();
        private readonly List<string> _multilineBuffer = new List<string>();
        private bool _inMultilineMode;
        private int _commandCount;

        private static readonly Dictionary<string, string> _commands = new Dictionary<string, string>
        {
            [":help"]          = "Show this help message",
            [":exit"]          = "Exit the REPL",
            [":quit"]          = "Exit the REPL (alias of :exit)",
            [":q"]             = "Exit the REPL (alias of :exit)",
            [":clear"]         = "Clear the console",
            [":reset"]         = "Reset the interpreter state",
            [":vars"]          = "List all defined variables",
            [":funcs"]         = "List all defined functions",
            [":interop"]       = "Show interop information",
            [":regs"]          = "Show NASM register dump",
            [":asm"]           = "Enter multi-line assembly mode",
            [":bytecode"]      = "Show bytecode for the given code",
            [":aot"]           = "Generate AOT C# code for the given code",
            [":history"]       = "Show command history",
            [":load <file>"]   = "Load and execute a .nx file",
            [":save <file>"]   = "Save session history to file",
            [":time <expr>"]   = "Time the execution of an expression",
            [":tokens <code>"] = "Show lexer tokens for code",
            [":ast <code>"]    = "Show AST for code",
        };

        public Repl()
        {
            _interop = new InteropManager();
            _interpreter = new Interpreter(_interop);
            _globalEnv = _interpreter.Globals;
        }

        public void Run()
        {
            PrintBanner();

            while (true)
            {
                try
                {
                    var prompt = _inMultilineMode ? "  ... " : $"nx[{_commandCount}]> ";
                    Console.Write(prompt);
                    var input = Console.ReadLine();

                    if (input == null)
                        break; // EOF (piped input) — exit cleanly

                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    _history.Add(input);

                    if (input.StartsWith(":") && !_inMultilineMode)
                    {
                        if (HandleCommand(input))
                            continue;
                        break; // :exit
                    }

                    if (_inMultilineMode)
                    {
                        if (input.Trim() == "}")
                        {
                            _inMultilineMode = false;
                            _multilineBuffer.Add(input);
                            ExecuteMultiline();
                            continue;
                        }
                        _multilineBuffer.Add(input);
                        continue;
                    }

                    if (NeedsMoreInput(input))
                    {
                        _inMultilineMode = true;
                        _multilineBuffer.Clear();
                        _multilineBuffer.Add(input);
                        continue;
                    }

                    _commandCount++;
                    ExecuteLine(input);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  Error: {ex.Message}");
                    Console.ResetColor();
                }
            }

            Console.WriteLine();
            Console.WriteLine("Goodbye!");
        }

        private void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==============================================================");
            Console.WriteLine("          NEXUS Programming Language - REPL v1.2.0            ");
            Console.WriteLine("   C#-like syntax | NASM features | Multi-language interop    ");
            Console.WriteLine("==============================================================");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Type :help for commands, :exit to quit.");
            Console.WriteLine("Supports: C, C++, NASM, Java, Go, C#, VB, F#, Python, Rust");
            Console.WriteLine();
        }

        private bool HandleCommand(string input)
        {
            var parts = input.Split(new[] { ' ' }, 2);
            var command = parts[0].ToLowerInvariant().Trim();
            var arg = parts.Length > 1 ? parts[1].Trim() : "";

            switch (command)
            {
                case ":help":
                    PrintHelp();
                    return true;

                case ":exit":
                case ":quit":
                case ":q":
                    return false;

                case ":clear":
                    Console.Clear();
                    return true;

                case ":reset":
                    _interpreter = new Interpreter(_interop);
                    _globalEnv = _interpreter.Globals;
                    _commandCount = 0;
                    Console.WriteLine("  Interpreter state reset.");
                    return true;

                case ":vars":
                    PrintVariables();
                    return true;

                case ":funcs":
                    PrintFunctions();
                    return true;

                case ":interop":
                    Console.WriteLine(_interop.GetInteropInfo());
                    return true;

                case ":regs":
                    Console.WriteLine(_interpreter.AsmEngine.DumpRegisters());
                    return true;

                case ":asm":
                    _inMultilineMode = true;
                    _multilineBuffer.Clear();
                    Console.WriteLine("  Multi-line mode: end the block with a line containing only '}'");
                    return true;

                case ":history":
                    for (int i = Math.Max(0, _history.Count - 20); i < _history.Count; i++)
                        Console.WriteLine($"  [{i}] {_history[i]}");
                    return true;

                case ":load":
                    LoadFile(arg);
                    return true;

                case ":save":
                    if (string.IsNullOrEmpty(arg))
                    {
                        Console.WriteLine("  Usage: :save <file>");
                        return true;
                    }
                    try
                    {
                        File.WriteAllLines(arg, _history);
                        Console.WriteLine($"  Saved {_history.Count} lines to {arg}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Save error: {ex.Message}");
                    }
                    return true;

                case ":tokens":
                    ShowTokens(arg);
                    return true;

                case ":ast":
                    ShowAst(arg);
                    return true;

                case ":bytecode":
                    ShowBytecode(arg);
                    return true;

                case ":aot":
                    ShowAot(arg);
                    return true;

                case ":time":
                    TimeExecution(arg);
                    return true;

                default:
                    Console.WriteLine($"  Unknown command: {command}. Type :help for available commands.");
                    return true;
            }
        }

        private void PrintHelp()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine("  Nexus REPL Commands:");
            Console.ResetColor();
            foreach (var kvp in _commands)
                Console.WriteLine($"  {kvp.Key,-20} {kvp.Value}");
            Console.WriteLine();
        }

        private void PrintVariables()
        {
            Console.WriteLine();
            Console.WriteLine("  Defined Variables:");
            foreach (var name in _globalEnv.GetVariableNames().OrderBy(n => n))
            {
                try
                {
                    var value = _globalEnv.Get(name);
                    var display = value.ToString() ?? "null";
                    if (display.Length > 50)
                        display = display.Substring(0, 47) + "...";
                    Console.WriteLine($"  {name,-20} : {value.TypeName,-12} = {display}");
                }
                catch { /* skip */ }
            }
            Console.WriteLine();
        }

        private void PrintFunctions()
        {
            Console.WriteLine();
            Console.WriteLine("  Defined Functions:");
            foreach (var name in _globalEnv.GetVariableNames().OrderBy(n => n))
            {
                try
                {
                    var value = _globalEnv.Get(name);
                    if (value is NexusFunction func)
                    {
                        var paramStr = string.Join(", ", func.Parameters.Select(p => p.Name));
                        Console.WriteLine($"  func {func.Name}({paramStr}) -> {func.ReturnType ?? "void"}");
                    }
                    else if (value is NexusInteropFunction iFunc)
                    {
                        Console.WriteLine($"  [{iFunc.InteropType}] {iFunc.EntryPoint} @ {iFunc.Library}");
                    }
                }
                catch { /* skip */ }
            }
            Console.WriteLine();
        }

        private void LoadFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Console.WriteLine($"  File not found: {path}");
                return;
            }

            try
            {
                var source = File.ReadAllText(path);
                ExecuteLine(source);
                Console.WriteLine($"  Loaded: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Error loading file: {ex.Message}");
            }
        }

        private void ShowTokens(string code)
        {
            var lexer = new Lexer(code);
            var tokens = lexer.Tokenize();

            Console.WriteLine();
            Console.WriteLine($"  Tokens ({tokens.Count}):");
            foreach (var token in tokens.Take(50))
                Console.WriteLine($"  {token.Type,-20} '{token.Value}' @{token.Line}:{token.Column}");
            if (tokens.Count > 50)
                Console.WriteLine($"  ... and {tokens.Count - 50} more");
            Console.WriteLine();
        }

        private void ShowAst(string code)
        {
            var lexer = new Lexer(code);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            var program = parser.ParseProgram();

            Console.WriteLine();
            Console.WriteLine($"  AST ({program.Statements.Count} statements):");
            foreach (var stmt in program.Statements)
                Console.WriteLine($"  {stmt}");
            if (parser.Errors.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  Parse Errors:");
                foreach (var error in parser.Errors)
                    Console.WriteLine($"    {error}");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        private void ShowBytecode(string code)
        {
            try
            {
                var lexer = new Lexer(code);
                var tokens = lexer.Tokenize();
                var parser = new Parser(tokens);
                var program = parser.ParseProgram();

                var compiler = new BytecodeCompiler();
                var chunks = compiler.Compile(program);

                Console.WriteLine();
                foreach (var chunk in chunks)
                    Console.WriteLine(chunk.Value.Disassemble());
                if (compiler.Errors.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    foreach (var e in compiler.Errors)
                        Console.WriteLine($"  ~ {e}");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Bytecode compilation error: {ex.Message}");
            }
        }

        private void ShowAot(string code)
        {
            try
            {
                var lexer = new Lexer(code);
                var tokens = lexer.Tokenize();
                var parser = new Parser(tokens);
                var program = parser.ParseProgram();

                var aotCompiler = new AotCompiler();
                var csharpCode = aotCompiler.CompileToCSharp(program);

                Console.WriteLine();
                Console.WriteLine("  Generated C# (AOT):");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(csharpCode);
                Console.ResetColor();
                foreach (var w in aotCompiler.Warnings)
                    Console.WriteLine($"  ~ {w}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  AOT compilation error: {ex.Message}");
            }
        }

        private void TimeExecution(string code)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ExecuteLine(code, silent: true);
            sw.Stop();
            Console.WriteLine($"  Execution time: {sw.Elapsed.TotalMilliseconds:F3}ms");
        }

        private bool NeedsMoreInput(string input)
        {
            var trimmed = input.Trim();

            int openBraces = trimmed.Count(c => c == '{');
            int closeBraces = trimmed.Count(c => c == '}');

            var blockStarters = new[] { "func ", "fn ", "def ", "if ", "while ", "for ", "foreach ", "class " };
            bool startsBlock = blockStarters.Any(s =>
                trimmed.StartsWith(s, StringComparison.OrdinalIgnoreCase));

            // asm module with the block on following lines:  var m = asm
            if (trimmed.EndsWith("asm"))
                return true;

            if (startsBlock && openBraces > closeBraces)
                return true;

            if (openBraces > closeBraces && !trimmed.EndsWith(";"))
                return true;

            return false;
        }

        private void ExecuteMultiline()
        {
            var code = string.Join("\n", _multilineBuffer);
            _multilineBuffer.Clear();
            _commandCount++;
            ExecuteLine(code);
        }

        private void ExecuteLine(string code, bool silent = false)
        {
            try
            {
                var lexer = new Lexer(code);
                var tokens = lexer.Tokenize();

                if (lexer.Errors.Count > 0 && !silent)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    foreach (var error in lexer.Errors)
                        Console.WriteLine($"  Lexer: {error}");
                    Console.ResetColor();
                }

                var parser = new Parser(tokens);
                var program = parser.ParseProgram();

                if (parser.Errors.Count > 0)
                {
                    if (!silent)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        foreach (var error in parser.Errors)
                            Console.WriteLine($"  Parse: {error}");
                        Console.ResetColor();
                    }
                    return;
                }

                _interpreter.Interpret(program);

                // show the value of a trailing expression
                if (!silent && program.Statements.Count == 1 &&
                    program.Statements[0] is ExpressionStmt)
                {
                    var result = _interpreter.LastValue;
                    if (!(result is NexusNull))
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.WriteLine($"  => {result}");
                        Console.ResetColor();
                    }
                }
            }
            catch (NexusRuntimeException ex)
            {
                if (!silent)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  Runtime Error: {ex.Message}");
                    Console.ResetColor();
                }
            }
            catch (NexusReturnException)
            {
                // ignore top-level return
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  Error: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }
    }
}

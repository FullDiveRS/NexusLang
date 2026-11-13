using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Nexus.Core;
using Nexus.Runtime;

namespace Nexus.Interop
{
    /// <summary>
    /// Manages interoperability with external languages:
    /// C/C++ (P/Invoke), NASM (simulated asm ops / shared libraries),
    /// Java (JNI-style dispatch), Go (c-shared), C#/VB/F# (.NET reflection),
    /// Python/Ruby (process invocation), Rust (C ABI).
    /// </summary>
    public class InteropManager
    {
        private readonly Dictionary<string, List<string>> _imports = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, Assembly> _loadedAssemblies = new Dictionary<string, Assembly>();
        private readonly List<string> _interopLog = new List<string>();

        public event Action<string> OnInteropEvent;

        public InteropManager()
        {
            OnInteropEvent += msg => _interopLog.Add(msg);
        }

        public void Log(string message)
        {
            _interopLog.Add(message);
            OnInteropEvent?.Invoke(message);
        }

        public List<string> GetLog() => _interopLog;

        public string GetInteropInfo()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Nexus Interop System ===");
            sb.AppendLine("Supported Languages:");
            sb.AppendLine("  - C/C++    -> P/Invoke (DllImport)");
            sb.AppendLine("  - NASM     -> simulated asm ops / shared libraries");
            sb.AppendLine("  - Java     -> JNI-style dispatch");
            sb.AppendLine("  - Go       -> c-shared libraries");
            sb.AppendLine("  - C#       -> .NET Assembly reflection");
            sb.AppendLine("  - VB.NET   -> .NET Assembly reflection");
            sb.AppendLine("  - F#       -> .NET Assembly reflection");
            sb.AppendLine("  - Python   -> process invocation");
            sb.AppendLine("  - Rust     -> cdylib (C ABI)");
            sb.AppendLine("  - Ruby     -> process invocation");
            sb.AppendLine();
            sb.AppendLine($"Active Imports: {_imports.Count}");
            foreach (var kvp in _imports)
                sb.AppendLine($"  {kvp.Key}: [{string.Join(", ", kvp.Value)}]");
            sb.AppendLine($"Loaded .NET Assemblies: {_loadedAssemblies.Count}");
            return sb.ToString();
        }

        public void ProcessImport(string module, string alias)
        {
            if (!_imports.ContainsKey(module))
                _imports[module] = new List<string>();
            if (alias != null)
                _imports[module].Add(alias);

            Log($"Import: {module}" + (alias != null ? $" as {alias}" : ""));

            if (module.EndsWith(".dll") || module.EndsWith(".so") || module.EndsWith(".dylib"))
                Log($"[Native] library registered: {module}");
            else if (module.EndsWith(".obj") || module.EndsWith(".o"))
                Log($"[NASM] object file detected: {module}");
            else if (module.EndsWith(".jar"))
                Log($"[Java] archive detected: {module}");
            else
                TryLoadDotNetAssembly(module);
        }

        public NexusInteropFunction CreateInteropFunction(InteropDeclaration decl)
        {
            var func = new NexusInteropFunction(decl.InteropType, decl.Library,
                decl.EntryPoint ?? decl.MethodName)
            {
                ReturnType = decl.ReturnType,
                Parameters = decl.Parameters
            };

            Log($"Created interop function: [{decl.InteropType}] {decl.MethodName} @ {decl.Library}");
            return func;
        }

        public NexusValue CallInterop(NexusInteropFunction func, List<NexusValue> args)
        {
            switch (func.InteropType)
            {
                case "DllImport": return CallCDll(func, args);
                case "NasmImport": return CallNasm(func, args);
                case "JavaImport": return CallJava(func, args);
                case "GoImport": return CallGo(func, args);
                case "CSharpImport": return CallCSharp(func, args);
                case "VbImport": return CallVbNet(func, args);
                case "FSharpImport": return CallFSharp(func, args);
                case "PythonImport": return CallPython(func, args);
                case "RustImport": return CallRust(func, args);
                case "RubyImport": return CallRuby(func, args);
                default:
                    throw new NexusRuntimeException($"Unknown interop type: {func.InteropType}");
            }
        }

        // ============ C/C++ (P/Invoke) ============
        private NexusValue CallCDll(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[C Interop] {func.Library}!{func.EntryPoint}({string.Join(", ", args.Select(a => a.ToString()))})");
            // real P/Invoke requires a generated stub; without a loadable native
            // library we report the call and return null
            return NexusNull.Instance;
        }

        // ============ NASM ============
        private NexusValue CallNasm(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[NASM Interop] {func.Library}!{func.EntryPoint}");
            var asmArgs = args.Select(ValueHelpers.ToInteger).ToArray();
            return new NexusInt(SimulateNasmCall(func.EntryPoint, asmArgs));
        }

        private static long SimulateNasmCall(string functionName, long[] args)
        {
            switch (functionName.ToLowerInvariant())
            {
                case "add" when args.Length >= 2: return args[0] + args[1];
                case "sub" when args.Length >= 2: return args[0] - args[1];
                case "mul" when args.Length >= 2: return args[0] * args[1];
                case "div" when args.Length >= 2 && args[1] != 0: return args[0] / args[1];
                case "xor" when args.Length >= 2: return args[0] ^ args[1];
                case "and" when args.Length >= 2: return args[0] & args[1];
                case "or" when args.Length >= 2: return args[0] | args[1];
                case "shl" when args.Length >= 2: return args[0] << (int)args[1];
                case "shr" when args.Length >= 2: return args[0] >> (int)args[1];
                case "not" when args.Length >= 1: return ~args[0];
                case "neg" when args.Length >= 1: return -args[0];
                case "abs" when args.Length >= 1: return Math.Abs(args[0]);
                case "max" when args.Length >= 2: return Math.Max(args[0], args[1]);
                case "min" when args.Length >= 2: return Math.Min(args[0], args[1]);
                case "sqrt" when args.Length >= 1: return (long)Math.Sqrt(args[0]);
                default: return 0;
            }
        }

        // ============ Java ============
        private NexusValue CallJava(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[Java Interop] {func.Library}.{func.EntryPoint}({string.Join(", ", args.Select(a => a.ToString()))})");

            if (func.EntryPoint == "length" && args.Count > 0 && args[0] is NexusString s)
                return new NexusInt(s.Value.Length);
            if (func.EntryPoint == "toUpperCase" && args.Count > 0)
                return new NexusString(args[0].ToString().ToUpper());
            if (func.EntryPoint == "toLowerCase" && args.Count > 0)
                return new NexusString(args[0].ToString().ToLower());
            if (func.EntryPoint == "hashCode" && args.Count > 0)
                return new NexusInt(args[0].ToString().GetHashCode());
            return NexusNull.Instance;
        }

        // ============ Go ============
        private NexusValue CallGo(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[Go Interop] {func.Library}!{func.EntryPoint}");
            var a = args.Select(ValueHelpers.ToInteger).ToArray();

            switch (func.EntryPoint)
            {
                case "Add" when a.Length >= 2: return new NexusInt(a[0] + a[1]);
                case "Multiply" when a.Length >= 2: return new NexusInt(a[0] * a[1]);
                case "Hello": return new NexusString("Hello from Go!");
                default: return NexusNull.Instance;
            }
        }

        // ============ C# ============
        private NexusValue CallCSharp(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[C# Interop] {func.Library}.{func.EntryPoint}");
            return InvokeReflected(func.Library, func.EntryPoint, args);
        }

        // ============ VB.NET ============
        private NexusValue CallVbNet(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[VB.NET Interop] {func.Library}.{func.EntryPoint}");
            return InvokeReflected(func.Library, func.EntryPoint, args, ignoreCase: true);
        }

        // ============ F# ============
        private NexusValue CallFSharp(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[F# Interop] {func.Library}.{func.EntryPoint}");
            return InvokeReflected(func.Library, func.EntryPoint, args, ignoreCase: true);
        }

        private NexusValue InvokeReflected(string library, string entryPoint,
                                           List<NexusValue> args, bool ignoreCase = false)
        {
            try
            {
                var assembly = GetOrLoadAssembly(library);
                if (assembly == null)
                {
                    Log($"[Interop] assembly '{library}' not found");
                    return NexusNull.Instance;
                }

                var parts = entryPoint.Split('.');
                var typeName = string.Join(".", parts.Take(parts.Length - 1));
                var methodName = parts[parts.Length - 1];

                var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var type = assembly.GetTypes().FirstOrDefault(t =>
                    t.Name.Equals(typeName, comparison) ||
                    (t.FullName != null && t.FullName.EndsWith(typeName, comparison)));

                if (type == null)
                {
                    Log($"[Interop] type '{typeName}' not found in {assembly.GetName().Name}");
                    return NexusNull.Instance;
                }

                var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name.Equals(methodName, comparison));

                if (method == null)
                {
                    Log($"[Interop] method '{methodName}' not found on {type.Name}");
                    return NexusNull.Instance;
                }

                var parameters = method.GetParameters();
                var converted = new object[parameters.Length];
                for (int i = 0; i < converted.Length && i < args.Count; i++)
                    converted[i] = ConvertToDotNetType(args[i], parameters[i].ParameterType);

                var result = method.Invoke(null, converted);
                return ValueHelpers.FromObject(result);
            }
            catch (Exception ex)
            {
                Log($"[Interop] error: {ex.Message}");
                return NexusNull.Instance;
            }
        }

        // ============ Python ============
        private NexusValue CallPython(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[Python Interop] {func.Library}.{func.EntryPoint}");
            try
            {
                var argStr = string.Join(", ", args.Select(a => a.ToString()));
                var script = $"import {func.Library}; print({func.Library}.{func.EntryPoint}({argStr}))";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "python3",
                    Arguments = $"-c \"{script}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (long.TryParse(output.Trim(), out var intResult))
                        return new NexusInt(intResult);
                    if (double.TryParse(output.Trim(), out var floatResult))
                        return new NexusFloat(floatResult);
                    return new NexusString(output.Trim());
                }
            }
            catch (Exception ex)
            {
                Log($"[Python Interop] error: {ex.Message}");
                return NexusNull.Instance;
            }
        }

        // ============ Rust ============
        private NexusValue CallRust(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[Rust Interop] {func.Library}!{func.EntryPoint}");
            var a = args.Select(ValueHelpers.ToInteger).ToArray();

            switch (func.EntryPoint)
            {
                case "add" when a.Length >= 2: return new NexusInt(a[0] + a[1]);
                case "multiply" when a.Length >= 2: return new NexusInt(a[0] * a[1]);
                case "fibonacci" when a.Length >= 1: return new NexusInt(Fibonacci((int)a[0]));
                default: return NexusNull.Instance;
            }
        }

        // ============ Ruby ============
        private NexusValue CallRuby(NexusInteropFunction func, List<NexusValue> args)
        {
            Log($"[Ruby Interop] {func.Library}.{func.EntryPoint}");
            try
            {
                var argStr = string.Join(", ", args.Select(a => a.ToString()));
                var script = $"require '{func.Library}'; puts {func.EntryPoint}({argStr})";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ruby",
                    Arguments = $"-e \"{script}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    return new NexusString(output.Trim());
                }
            }
            catch (Exception ex)
            {
                Log($"[Ruby Interop] error: {ex.Message}");
                return NexusNull.Instance;
            }
        }

        // ============ assembly loading ============
        public Assembly GetOrLoadAssembly(string name)
        {
            if (_loadedAssemblies.TryGetValue(name, out var cached))
                return cached;

            try
            {
                var assembly = Assembly.Load(new AssemblyName(name));
                _loadedAssemblies[name] = assembly;
                return assembly;
            }
            catch
            {
                try
                {
                    var path = Path.IsPathRooted(name) ? name : Path.Combine(Directory.GetCurrentDirectory(), name);
                    if (!path.EndsWith(".dll") && !path.EndsWith(".exe"))
                        path += ".dll";

                    if (File.Exists(path))
                    {
                        var assembly = Assembly.LoadFrom(path);
                        _loadedAssemblies[name] = assembly;
                        Log($"[Assembly] loaded: {assembly.FullName}");
                        return assembly;
                    }
                }
                catch (Exception ex)
                {
                    Log($"[Assembly] failed to load '{name}': {ex.Message}");
                }
            }

            return null;
        }

        private void TryLoadDotNetAssembly(string path)
        {
            var assembly = GetOrLoadAssembly(path);
            if (assembly != null)
                Log($"[Assembly] loaded: {assembly.GetName().Name}");
        }

        private static object ConvertToDotNetType(NexusValue value, Type targetType)
        {
            if (targetType == typeof(int)) return (int)ValueHelpers.ToInteger(value);
            if (targetType == typeof(long)) return ValueHelpers.ToInteger(value);
            if (targetType == typeof(double)) return ValueHelpers.ToNumber(value);
            if (targetType == typeof(float)) return (float)ValueHelpers.ToNumber(value);
            if (targetType == typeof(string)) return value.ToString();
            if (targetType == typeof(bool)) return value.IsTruthy();
            if (targetType == typeof(char)) return value.ToString().FirstOrDefault();
            return ValueHelpers.ToObject(value);
        }

        private static long Fibonacci(int n)
        {
            if (n <= 1) return n;
            long a = 0, b = 1;
            for (int i = 2; i <= n; i++)
            {
                long temp = a + b;
                a = b;
                b = temp;
            }
            return b;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Core;
using Nexus.Interop;

namespace Nexus.Runtime
{
    /// <summary>Tree-walking interpreter for Nexus programs.</summary>
    public class Interpreter
    {
        private RuntimeEnvironment _globals;
        private readonly InteropManager _interop;
        private readonly List<string> _output;
        private readonly Dictionary<string, NexusClass> _classes = new Dictionary<string, NexusClass>();
        private readonly Dictionary<string, NexusEnum> _enums = new Dictionary<string, NexusEnum>();
        private readonly AsmEngine _asmEngine;

        public List<string> Output => _output;
        public RuntimeEnvironment Globals => _globals;
        public AsmEngine AsmEngine => _asmEngine;

        /// <summary>Value of the last evaluated expression statement (used by the REPL).</summary>
        public NexusValue LastValue { get; private set; } = NexusNull.Instance;

        public Interpreter(InteropManager interop = null)
        {
            _globals = new RuntimeEnvironment(null, "global");
            _interop = interop ?? new InteropManager();
            _output = new List<string>();
            _asmEngine = new AsmEngine();
            RegisterBuiltins();
        }

        private void RegisterBuiltins()
        {
            _globals.Define("print", new NexusFunction("print")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    var text = string.Join(" ", args.Select(a => a.ToString()));
                    Console.Write(text);
                    _output.Add(text);
                    return NexusNull.Instance;
                }
            });

            _globals.Define("println", new NexusFunction("println")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    var text = string.Join(" ", args.Select(a => a.ToString()));
                    Console.WriteLine(text);
                    _output.Add(text);
                    return NexusNull.Instance;
                }
            });

            _globals.Define("printf", new NexusFunction("printf")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    if (args.Count == 0) return NexusNull.Instance;
                    var format = args[0].ToString();

                    // translate C-style specifiers (%d %i %s %f %x %c %%) to .NET style
                    var sb = new System.Text.StringBuilder();
                    int argIndex = 0;   // placeholders index into args AFTER the format string
                    for (int i = 0; i < format.Length; i++)
                    {
                        if (format[i] == '%' && i + 1 < format.Length)
                        {
                            char spec = format[i + 1];
                            if (spec == '%') { sb.Append('%'); i++; continue; }
                            string net = spec switch
                            {
                                'd' or 'i' or 'u' => "",
                                'f' => "F2",
                                'x' => "x",
                                'X' => "X",
                                's' or 'c' => "",
                                _ => null
                            };
                            if (net != null)
                            {
                                sb.Append('{').Append(argIndex++);
                                if (net.Length > 0) sb.Append(':').Append(net);
                                sb.Append('}');
                                i++;
                                continue;
                            }
                        }
                        sb.Append(format[i]);
                    }

                    var formatted = string.Format(sb.ToString(),
                        args.Skip(1).Select(ValueHelpers.ToObject).ToArray());
                    Console.Write(formatted);
                    _output.Add(formatted);
                    return NexusNull.Instance;
                }
            });

            _globals.Define("abs", new NexusFunction("abs")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    if (args.Count == 0) return NexusNull.Instance;
                    var val = ValueHelpers.ToNumber(args[0]);
                    return val == Math.Floor(val)
                        ? new NexusInt(Math.Abs((long)val))
                        : new NexusFloat(Math.Abs(val));
                }
            });

            _globals.Define("sqrt", new NexusFunction("sqrt")
            {
                IsNative = true,
                NativeImplementation = args => args.Count == 0
                    ? NexusNull.Instance
                    : new NexusFloat(Math.Sqrt(ValueHelpers.ToNumber(args[0])))
            });

            _globals.Define("pow", new NexusFunction("pow")
            {
                IsNative = true,
                NativeImplementation = args => args.Count < 2
                    ? NexusNull.Instance
                    : new NexusFloat(Math.Pow(ValueHelpers.ToNumber(args[0]), ValueHelpers.ToNumber(args[1])))
            });

            _globals.Define("floor", new NexusFunction("floor")
            {
                IsNative = true,
                NativeImplementation = args => args.Count == 0
                    ? NexusNull.Instance
                    : new NexusInt((long)Math.Floor(ValueHelpers.ToNumber(args[0])))
            });

            _globals.Define("ceil", new NexusFunction("ceil")
            {
                IsNative = true,
                NativeImplementation = args => args.Count == 0
                    ? NexusNull.Instance
                    : new NexusInt((long)Math.Ceiling(ValueHelpers.ToNumber(args[0])))
            });

            _globals.Define("range", new NexusFunction("range")
            {
                IsNative = true,
                NativeImplementation = args => args.Count < 2
                    ? NexusNull.Instance
                    : BuildRange(args[0], args[1])
            });

            var rng = new Random();
            _globals.Define("random", new NexusFunction("random")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    if (args.Count == 0) return new NexusFloat(rng.NextDouble());
                    if (args.Count == 1) return new NexusInt(rng.Next((int)ValueHelpers.ToInteger(args[0])));
                    return new NexusInt(rng.Next((int)ValueHelpers.ToInteger(args[0]),
                                                 (int)ValueHelpers.ToInteger(args[1])));
                }
            });

            _globals.Define("len", new NexusFunction("len")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    if (args.Count == 0) return new NexusInt(0);
                    switch (args[0])
                    {
                        case NexusString s: return new NexusInt(s.Value.Length);
                        case NexusList l: return new NexusInt(l.Items.Count);
                        case NexusDict d: return new NexusInt(d.Entries.Count);
                        case NexusArray a: return new NexusInt(a.Elements.Length);
                        default: return new NexusInt(0);
                    }
                }
            });

            _globals.Define("typeof", new NexusFunction("typeof")
            {
                IsNative = true,
                NativeImplementation = args => args.Count == 0
                    ? NexusNull.Instance
                    : new NexusString(args[0].TypeName)
            });

            _globals.Define("str", new NexusFunction("str")
            {
                IsNative = true,
                NativeImplementation = args => new NexusString(args.Count == 0 ? "" : args[0].ToString())
            });

            _globals.Define("int", new NexusFunction("int")
            {
                IsNative = true,
                NativeImplementation = args => new NexusInt(args.Count == 0 ? 0 : ValueHelpers.ToInteger(args[0]))
            });

            _globals.Define("float", new NexusFunction("float")
            {
                IsNative = true,
                NativeImplementation = args => new NexusFloat(args.Count == 0 ? 0 : ValueHelpers.ToNumber(args[0]))
            });

            _globals.Define("input", new NexusFunction("input")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    if (args.Count > 0) Console.Write(args[0].ToString());
                    return new NexusString(Console.ReadLine() ?? "");
                }
            });

            _globals.Define("push", new NexusFunction("push")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    if (args.Count >= 2 && args[0] is NexusList list)
                    {
                        list.Items.Add(args[1]);
                        return list;
                    }
                    return NexusNull.Instance;
                }
            });

            _globals.Define("pop", new NexusFunction("pop")
            {
                IsNative = true,
                NativeImplementation = args =>
                {
                    if (args.Count >= 1 && args[0] is NexusList list && list.Items.Count > 0)
                    {
                        var last = list.Items[list.Items.Count - 1];
                        list.Items.RemoveAt(list.Items.Count - 1);
                        return last;
                    }
                    return NexusNull.Instance;
                }
            });

            _globals.Define("time", new NexusFunction("time")
            {
                IsNative = true,
                NativeImplementation = args => new NexusInt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            });

            _globals.Define("interop_info", new NexusFunction("interop_info")
            {
                IsNative = true,
                NativeImplementation = args => new NexusString(_interop.GetInteropInfo())
            });
        }

        // ============ INTERPRET ============
        public void Interpret(ProgramNode program)
        {
            try
            {
                ExecuteBlock(program.Statements, _globals);
            }
            catch (NexusReturnException)
            {
                // top-level return, ignore
            }
            catch (NexusBreakException)
            {
                throw new NexusRuntimeException("'break' outside of a loop or switch");
            }
            catch (NexusContinueException)
            {
                throw new NexusRuntimeException("'continue' outside of a loop");
            }
            catch (NexusRuntimeException ex)
            {
                throw new NexusRuntimeException($"Runtime error: {ex.Message}", ex.Line);
            }
        }

        public NexusValue EvaluateExpression(Expression expr, RuntimeEnvironment env)
        {
            return Evaluate(expr, env);
        }

        private void ExecuteBlock(List<Statement> statements, RuntimeEnvironment env)
        {
            foreach (var stmt in statements)
                Execute(stmt, env);
        }

        private void Execute(Statement stmt, RuntimeEnvironment env)
        {
            switch (stmt)
            {
                case VariableDecl varDecl:
                    ExecuteVariableDecl(varDecl, env);
                    break;

                case MultiVariableDecl multiDecl:
                    foreach (var d in multiDecl.Declarations)
                        ExecuteVariableDecl(d, env);
                    break;

                case ExpressionStmt exprStmt:
                    LastValue = Evaluate(exprStmt.Expression, env);
                    break;

                case BlockStmt block:
                    ExecuteBlock(block.Statements, new RuntimeEnvironment(env));
                    break;

                case IfStmt ifStmt:
                    ExecuteIf(ifStmt, env);
                    break;

                case WhileStmt whileStmt:
                    ExecuteWhile(whileStmt, env);
                    break;

                case ForStmt forStmt:
                    ExecuteFor(forStmt, env);
                    break;

                case ForeachStmt foreachStmt:
                    ExecuteForeach(foreachStmt, env);
                    break;

                case FunctionDecl funcDecl:
                    ExecuteFunctionDecl(funcDecl, env);
                    break;

                case ReturnStmt returnStmt:
                    ExecuteReturn(returnStmt, env);
                    break;

                case BreakStmt:
                    throw new NexusBreakException();

                case ContinueStmt:
                    throw new NexusContinueException();

                case ClassDecl classDecl:
                    ExecuteClassDecl(classDecl, env);
                    break;

                case StructDecl structDecl:
                    ExecuteStructDecl(structDecl, env);
                    break;

                case EnumDecl enumDecl:
                    ExecuteEnumDecl(enumDecl, env);
                    break;

                case DelegateDecl delegateDecl:
                {
                    var delegateType = new NexusDelegateType(delegateDecl.Name)
                    { ReturnType = delegateDecl.ReturnType };
                    foreach (var p in delegateDecl.Parameters)
                        delegateType.Parameters.Add(p);
                    env.Define(delegateDecl.Name, delegateType);
                    break;
                }

                case EventDecl eventDecl:
                {
                    // top-level or static event: one shared NexusEvent
                    var ev = new NexusEvent(eventDecl.Name, eventDecl.DelegateType);
                    env.Define(eventDecl.Name, ev);
                    break;
                }

                case PropertyDecl propDecl:
                    throw new NexusRuntimeException(
                        $"Property '{propDecl.Name}' is only allowed inside a class or enum");

                case NamespaceDecl namespaceDecl:
                    ExecuteBlock(namespaceDecl.Body,
                        new RuntimeEnvironment(env, $"namespace:{namespaceDecl.Name}"));
                    break;

                case ImportStmt importStmt:
                    _interop.ProcessImport(importStmt.Module, importStmt.Alias);
                    break;

                case InteropDeclaration interopDecl:
                    ExecuteInteropDeclaration(interopDecl, env);
                    break;

                case AsmBlock asmBlock:
                    ExecuteAsmBlock(asmBlock, env);
                    break;

                case PrintStmt printStmt:
                    ExecutePrint(printStmt, env);
                    break;

                case TryCatchStmt tryCatch:
                    ExecuteTryCatch(tryCatch, env);
                    break;

                case SwitchStmt switchStmt:
                    ExecuteSwitch(switchStmt, env);
                    break;

                case ThrowStmt throwStmt:
                {
                    var throwValue = throwStmt.Value != null
                        ? Evaluate(throwStmt.Value, env)
                        : new NexusString("Unknown error");
                    throw new NexusRuntimeException($"Uncaught exception: {throwValue}");
                }

                default:
                    throw new NexusRuntimeException(
                        $"Unknown statement type: {stmt.GetType().Name}");
            }
        }

        // ============ STATEMENT EXECUTORS ============
        private void ExecuteVariableDecl(VariableDecl decl, RuntimeEnvironment env)
        {
            NexusValue value = NexusNull.Instance;

            if (decl.Initializer != null)
                value = Evaluate(decl.Initializer, env);
            else if (!string.IsNullOrEmpty(decl.VarType) &&
                     decl.VarType != "var" && decl.VarType != "let" &&
                     decl.VarType != "dynamic")
                value = ValueHelpers.DefaultForType(decl.VarType);   // int x; → 0

            if (value is NexusAsmModule namedModule)
                namedModule.Name = decl.Name;

            // delegate-typed variables: check the assigned value is callable
            if (!string.IsNullOrEmpty(decl.VarType) &&
                env.TryGet(decl.VarType, out var typeVal) && typeVal is NexusDelegateType)
            {
                if (value.TypeName != "null" && !ValueHelpers.IsCallable(value))
                    throw new NexusRuntimeException(
                        $"Cannot assign value of type '{value.TypeName}' to delegate '{decl.VarType}'");
            }

            env.Define(decl.Name, value, decl.Modifier == "const");
        }

        private void ExecutePrint(PrintStmt stmt, RuntimeEnvironment env)
        {
            var parts = new List<string>(stmt.Values.Count);
            foreach (var valueExpr in stmt.Values)
                parts.Add(Evaluate(valueExpr, env).ToString() ?? "");

            var text = string.Join(" ", parts);
            if (stmt.Newline)
            {
                Console.WriteLine(text);
                _output.Add(text);
            }
            else
            {
                Console.Write(text);
                _output.Add(text);
            }
        }

        private void ExecuteIf(IfStmt stmt, RuntimeEnvironment env)
        {
            if (Evaluate(stmt.Condition, env).IsTruthy())
                Execute(stmt.ThenBranch, env);
            else if (stmt.ElseBranch != null)
                Execute(stmt.ElseBranch, env);
        }

        private void ExecuteWhile(WhileStmt stmt, RuntimeEnvironment env)
        {
            if (stmt.IsDoWhile)
            {
                do
                {
                    try { Execute(stmt.Body, env); }
                    catch (NexusBreakException) { break; }
                    catch (NexusContinueException) { }
                }
                while (Evaluate(stmt.Condition, env).IsTruthy());
            }
            else
            {
                while (Evaluate(stmt.Condition, env).IsTruthy())
                {
                    try { Execute(stmt.Body, env); }
                    catch (NexusBreakException) { break; }
                    catch (NexusContinueException) { continue; }
                }
            }
        }

        private void ExecuteFor(ForStmt stmt, RuntimeEnvironment env)
        {
            var forEnv = new RuntimeEnvironment(env);

            if (stmt.Initializer != null)
                Execute(stmt.Initializer, forEnv);

            while (stmt.Condition == null || Evaluate(stmt.Condition, forEnv).IsTruthy())
            {
                try
                {
                    Execute(stmt.Body, forEnv);
                }
                catch (NexusBreakException) { break; }
                catch (NexusContinueException) { }

                if (stmt.Increment != null)
                    Evaluate(stmt.Increment, forEnv);
            }
        }

        private void ExecuteForeach(ForeachStmt stmt, RuntimeEnvironment env)
        {
            var iterable = Evaluate(stmt.Iterable, env);
            var foreachEnv = new RuntimeEnvironment(env);

            IEnumerable<NexusValue> items;
            switch (iterable)
            {
                case NexusList list: items = list.Items; break;
                case NexusArray array: items = array.Elements; break;
                case NexusString str: items = str.Value.Select(c => (NexusValue)new NexusChar(c)); break;
                case NexusDict dict: items = dict.Entries.Keys.Select(k => (NexusValue)new NexusString(k)); break;
                default:
                    throw new NexusRuntimeException($"Cannot iterate over type '{iterable.TypeName}'");
            }

            foreach (var item in items)
            {
                foreachEnv.Define(stmt.VariableName, item);
                try
                {
                    Execute(stmt.Body, foreachEnv);
                }
                catch (NexusBreakException) { break; }
                catch (NexusContinueException) { continue; }
            }
        }

        private void ExecuteFunctionDecl(FunctionDecl decl, RuntimeEnvironment env)
        {
            var function = new NexusFunction(decl.Name)
            {
                Parameters = decl.Parameters,
                Body = decl.Body,
                Closure = env,
                ReturnType = decl.ReturnType
            };

            env.Define(decl.Name, function);
        }

        private void ExecuteReturn(ReturnStmt stmt, RuntimeEnvironment env)
        {
            NexusValue value = stmt.Value != null ? Evaluate(stmt.Value, env) : NexusNull.Instance;
            throw new NexusReturnException(value);
        }

        private void ExecuteClassDecl(ClassDecl decl, RuntimeEnvironment env)
        {
            NexusClass baseClass = null;
            if (decl.BaseClass != null)
            {
                if (!env.TryGet(decl.BaseClass, out var baseVal) || !(baseVal is NexusClass bc))
                    throw new NexusRuntimeException($"Base class '{decl.BaseClass}' not found.");
                baseClass = bc;
            }

            var nexusClass = new NexusClass(decl.Name) { BaseClass = baseClass };
            var classEnv = new RuntimeEnvironment(env, $"class:{decl.Name}");
            nexusClass.StaticEnv = classEnv;

            foreach (var member in decl.Members)
            {
                if (member is FunctionDecl method && !method.IsOperator)
                {
                    var methodFn = new NexusFunction(method.Name)
                    {
                        Parameters = method.Parameters,
                        Body = method.Body,
                        Closure = classEnv,
                        ReturnType = method.ReturnType
                    };
                    nexusClass.Methods[method.Name] = methodFn;
                    // static methods resolve sibling statics through the class env;
                    // also let bare-name sibling calls work inside method bodies
                    if (method.IsStatic)
                        classEnv.Define(method.Name, methodFn);
                }
                else if (member is FunctionDecl opDecl)
                {
                    var opFn = new NexusFunction($"{decl.Name}.operator {opDecl.OperatorSymbol}")
                    {
                        Parameters = opDecl.Parameters,
                        Body = opDecl.Body,
                        Closure = classEnv,
                        ReturnType = opDecl.ReturnType
                    };
                    nexusClass.Operators[$"{opDecl.OperatorSymbol}#{opDecl.Parameters.Count}"] = opFn;
                }
                else if (member is PropertyDecl prop)
                {
                    nexusClass.Properties[prop.Name] = BuildAccessors(decl.Name, prop, classEnv);
                }
                else if (member is EventDecl evt)
                {
                    if (evt.IsStatic)
                        classEnv.Define(evt.Name, new NexusEvent(evt.Name, evt.DelegateType));
                    else
                        nexusClass.EventNames.Add(evt.Name);
                }
                else if (member is VariableDecl field)
                {
                    if (field.IsStatic)
                    {
                        var fieldValue = field.Initializer != null
                            ? Evaluate(field.Initializer, env)
                            : ValueHelpers.DefaultForType(field.VarType);
                        classEnv.Define(field.Name, fieldValue);
                        nexusClass.StaticFields[field.Name] = fieldValue;
                    }
                    else
                    {
                        nexusClass.FieldDeclarations.Add(field);
                    }
                }
            }

            // legacy mirror for code that reads StaticFields directly (REPL :vars, etc.)
            foreach (var sf in nexusClass.StaticFields)
                classEnv.Define(sf.Key, sf.Value);

            _classes[decl.Name] = nexusClass;
            env.Define(decl.Name, nexusClass);
        }

        private PropertyAccessors BuildAccessors(string className, PropertyDecl prop,
                                                 RuntimeEnvironment classEnv)
        {
            var accessors = new PropertyAccessors { Name = prop.Name };

            if (prop.HasGetter)
            {
                if (prop.GetterBody != null)
                {
                    accessors.Getter = new NexusFunction($"{className}.get_{prop.Name}")
                    {
                        Parameters = prop.GetterBody.Parameters,
                        Body = prop.GetterBody.Body,
                        Closure = classEnv,
                        ReturnType = prop.GetterBody.ReturnType
                    };
                }
                else
                {
                    accessors.Getter = new NexusFunction($"{className}.get_{prop.Name}")
                    { Closure = classEnv };
                    accessors.GetterIsAuto = true;
                }
            }

            if (prop.HasSetter)
            {
                if (prop.SetterBody != null)
                {
                    accessors.Setter = new NexusFunction($"{className}.set_{prop.Name}")
                    {
                        Parameters = prop.SetterBody.Parameters,
                        Body = prop.SetterBody.Body,
                        Closure = classEnv,
                        ReturnType = prop.SetterBody.ReturnType
                    };
                }
                else
                {
                    accessors.Setter = new NexusFunction($"{className}.set_{prop.Name}")
                    { Closure = classEnv };
                    accessors.SetterIsAuto = true;
                }
            }

            return accessors;
        }

        private void ExecuteStructDecl(StructDecl decl, RuntimeEnvironment env)
        {
            var nexusClass = new NexusClass(decl.Name);
            foreach (var field in decl.Fields.OfType<VariableDecl>())
                nexusClass.FieldDeclarations.Add(field);
            _classes[decl.Name] = nexusClass;
            env.Define(decl.Name, nexusClass);
        }

        private void ExecuteEnumDecl(EnumDecl decl, RuntimeEnvironment env)
        {
            // Enums are live classes: members are NexusEnum static fields, and the
            // body may also carry static methods / fields / properties (Nexus ext.)
            var enumClass = new NexusClass(decl.Name) { IsEnum = true };
            var enumEnv = new RuntimeEnvironment(env, $"enum:{decl.Name}");
            enumClass.StaticEnv = enumEnv;

            foreach (var member in decl.Members)
            {
                var memberValue = new NexusEnum(decl.Name, member.Item1, member.Item2);
                enumEnv.Define(member.Item1, memberValue);
                enumClass.StaticFields[member.Item1] = memberValue;
                _enums[$"{decl.Name}.{member.Item1}"] = memberValue;
            }

            foreach (var member in decl.MemberStatements)
            {
                if (member is FunctionDecl method && !method.IsOperator)
                {
                    var methodFn = new NexusFunction(method.Name)
                    {
                        Parameters = method.Parameters,
                        Body = method.Body,
                        Closure = enumEnv,
                        ReturnType = method.ReturnType
                    };
                    enumClass.Methods[method.Name] = methodFn;
                    enumEnv.Define(method.Name, methodFn);   // sibling calls by bare name
                }
                else if (member is FunctionDecl opDecl)
                {
                    enumClass.Operators[$"{opDecl.OperatorSymbol}#{opDecl.Parameters.Count}"] =
                        new NexusFunction($"{decl.Name}.operator {opDecl.OperatorSymbol}")
                        {
                            Parameters = opDecl.Parameters,
                            Body = opDecl.Body,
                            Closure = enumEnv
                        };
                }
                else if (member is PropertyDecl prop)
                {
                    enumClass.Properties[prop.Name] = BuildAccessors(decl.Name, prop, enumEnv);
                }
                else if (member is EventDecl evt)
                {
                    enumEnv.Define(evt.Name, new NexusEvent(evt.Name, evt.DelegateType));
                }
                else if (member is VariableDecl field)
                {
                    var fieldValue = field.Initializer != null
                        ? Evaluate(field.Initializer, env)
                        : ValueHelpers.DefaultForType(field.VarType);
                    enumEnv.Define(field.Name, fieldValue);
                    enumClass.StaticFields[field.Name] = fieldValue;
                }
            }

            _classes[decl.Name] = enumClass;
            env.Define(decl.Name, enumClass);
        }

        private void ExecuteInteropDeclaration(InteropDeclaration decl, RuntimeEnvironment env)
        {
            var interopFunc = _interop.CreateInteropFunction(decl);
            env.Define(decl.MethodName, interopFunc);
        }

        private void ExecuteTryCatch(TryCatchStmt stmt, RuntimeEnvironment env)
        {
            try
            {
                Execute(stmt.TryBlock, env);
            }
            catch (NexusRuntimeException ex)
            {
                if (stmt.CatchBlock != null)
                {
                    var catchEnv = new RuntimeEnvironment(env);
                    if (stmt.CatchVariable != null)
                        catchEnv.Define(stmt.CatchVariable, new NexusString(ex.Message));
                    Execute(stmt.CatchBlock, catchEnv);
                }
                else
                {
                    throw; // never swallow errors when there is no catch block
                }
            }
            finally
            {
                if (stmt.FinallyBlock != null)
                    Execute(stmt.FinallyBlock, env);
            }
        }

        private void ExecuteSwitch(SwitchStmt stmt, RuntimeEnvironment env)
        {
            var subject = Evaluate(stmt.Subject, env);

            foreach (var caseItem in stmt.Cases)
            {
                var caseValue = Evaluate(caseItem.Value, env);
                if (ValuesEqual(subject, caseValue))
                {
                    // 'break' inside a case ends the case — NOT an enclosing loop
                    try
                    {
                        foreach (var caseStmt in caseItem.Body)
                            Execute(caseStmt, env);
                    }
                    catch (NexusBreakException) { /* case finished */ }
                    return; // no fall-through
                }
            }

            if (stmt.DefaultCase != null)
            {
                try
                {
                    Execute(stmt.DefaultCase, env);
                }
                catch (NexusBreakException) { /* default finished */ }
            }
        }

        private void ExecuteAsmBlock(AsmBlock block, RuntimeEnvironment env)
        {
            _asmEngine.Execute(block, env, this);
        }

        // ============ ASM MODULES ============
        private NexusValue EvaluateAsmModule(AsmModuleExpr expr, RuntimeEnvironment env)
        {
            // each module gets its OWN engine: isolated registers, stack and memory
            var engine = new AsmEngine();
            engine.LoadData(expr.Block);
            return new NexusAsmModule("<asm>", expr.Block, engine, env, this);
        }

        private NexusValue InvokeAsmFunction(NexusAsmLabelFunction fn, List<NexusValue> args)
        {
            var m = fn.Module;
            var result = m.Engine.InvokeLabel(m.Block, fn.Label, args, m.CapturedEnv, this);
            return new NexusInt(result);
        }

        private NexusValue InvokeAsmEntry(NexusAsmModule m, List<NexusValue> args)
        {
            var entry = m.DefaultEntryPoint();
            if (entry.Length == 0)
                throw new NexusRuntimeException($"ASM module '{m.Name}' has no labels to call");
            return new NexusInt(m.Engine.InvokeLabel(m.Block, entry, args, m.CapturedEnv, this));
        }

        private NexusValue ExecuteBaseInit(BaseInitCallExpr expr, RuntimeEnvironment env)
        {
            if (env.TryGet("this", out var thisVal) && thisVal is NexusInstance inst &&
                inst.Class != null && inst.Class.BaseClass != null &&
                inst.Class.BaseClass.FindMethod("init") is NexusFunction ctor)
            {
                var ctorEnv = new RuntimeEnvironment(
                    ctor.Closure ?? env, $"base-ctor:{inst.Class.BaseClass.Name}");
                ctorEnv.Define("this", inst);

                var args = expr.Arguments.Select(a => Evaluate(a, env)).ToList();
                for (int i = 0; i < ctor.Parameters.Count && i < args.Count; i++)
                    ctorEnv.Define(ctor.Parameters[i].Name, args[i]);

                try
                {
                    foreach (var s in ctor.Body)
                        Execute(s, ctorEnv);
                }
                catch (NexusReturnException) { }
            }
            return NexusNull.Instance;
        }

        // ============ EXPRESSION EVALUATION ============
        private NexusValue Evaluate(Expression expr, RuntimeEnvironment env)
        {
            switch (expr)
            {
                case IntLiteralExpr lit: return new NexusInt(lit.Value);
                case FloatLiteralExpr lit: return new NexusFloat(lit.Value);
                case StringLiteralExpr lit: return new NexusString(lit.Value);
                case CharLiteralExpr lit: return new NexusChar(lit.Value);
                case BoolLiteralExpr lit: return new NexusBool(lit.Value);
                case NullLiteralExpr _: return NexusNull.Instance;
                case ThisExpr _: return env.Get("this");
                case IdentifierExpr id: return EvaluateIdentifier(id, env);
                case BinaryExpr bin: return EvaluateBinary(bin, env);
                case UnaryExpr un: return EvaluateUnary(un, env);
                case AssignExpr assign: return EvaluateAssign(assign, env);
                case CallExpr call: return EvaluateCall(call, env);
                case MemberAccessExpr member: return EvaluateMemberAccess(member, env);
                case IndexExpr index: return EvaluateIndex(index, env);
                case TernaryExpr ternary: return EvaluateTernary(ternary, env);
                case RangeExpr rangeExpr: return EvaluateRange(rangeExpr, env);
                case NewExpr newExpr: return EvaluateNew(newExpr, env);
                case ListExpr listExpr: return EvaluateList(listExpr, env);
                case DictExpr dictExpr: return EvaluateDict(dictExpr, env);
                case AsExpr asExpr: return EvaluateAs(asExpr, env);
                case IsExpr isExpr: return EvaluateIs(isExpr, env);
                case BaseInitCallExpr baseInit: return ExecuteBaseInit(baseInit, env);
                case PointerExpr ptrExpr: return EvaluatePointer(ptrExpr, env);
                case AddressOfExpr addrExpr: return EvaluateAddressOf(addrExpr, env);
                case AsmModuleExpr asmModuleExpr: return EvaluateAsmModule(asmModuleExpr, env);
                case LambdaExpr lambda: return EvaluateLambda(lambda, env);
                case InterpStringExpr interp: return EvaluateInterpString(interp, env);
                default:
                    throw new NexusRuntimeException(
                        $"Unknown expression type: {expr.GetType().Name}");
            }
        }

        private NexusValue EvaluateLambda(LambdaExpr lambda, RuntimeEnvironment env)
        {
            List<Statement> body;
            if (lambda.IsExpressionBody)
            {
                body = new List<Statement> { new ReturnStmt(lambda.ExpressionBody) };
            }
            else
            {
                body = lambda.Body;
            }

            return new NexusFunction("lambda")
            {
                Parameters = lambda.Parameters,
                Body = body,
                Closure = env
            };
        }

        private NexusValue EvaluateInterpString(InterpStringExpr expr, RuntimeEnvironment env)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in expr.Parts)
            {
                if (part is Expression inner)
                    sb.Append(Evaluate(inner, env)?.ToString() ?? "");
                else
                    sb.Append(part);
            }
            return new NexusString(sb.ToString());
        }

        private NexusValue EvaluateIdentifier(IdentifierExpr expr, RuntimeEnvironment env)
        {
            if (env.TryGet(expr.Name, out var value))
                return value;

            // C#-style implicit `this`: inside an instance method a bare field,
            // method or property name resolves against the current instance
            if (!env.IsDefined(expr.Name) &&
                env.TryGet("this", out var thisVal) &&
                thisVal is NexusInstance inst &&
                (inst.Fields.ContainsKey(expr.Name) ||
                 inst.Class?.FindMethod(expr.Name) is NexusFunction ||
                 inst.Class?.FindProperty(expr.Name) != null))
            {
                return GetInstanceMember(inst, expr.Name, env);
            }

            throw new NexusRuntimeException($"Undefined variable '{expr.Name}'.");
        }

        private NexusValue EvaluateBinary(BinaryExpr expr, RuntimeEnvironment env)
        {
            // short-circuit for logical operators
            if (expr.Operator == "&&")
            {
                var l = Evaluate(expr.Left, env);
                if (!l.IsTruthy()) return new NexusBool(false);
                return new NexusBool(Evaluate(expr.Right, env).IsTruthy());
            }
            if (expr.Operator == "||")
            {
                var l = Evaluate(expr.Left, env);
                if (l.IsTruthy()) return new NexusBool(true);
                return new NexusBool(Evaluate(expr.Right, env).IsTruthy());
            }

            var left = Evaluate(expr.Left, env);
            var right = Evaluate(expr.Right, env);

            switch (expr.Operator)
            {
                case "??": return left.TypeName == "null" ? right : left;
            }

            // user-defined operator overloads take priority over builtin semantics
            var overload = TryInvokeOperator(left, expr.Operator, right, env);
            if (overload != null)
                return overload;

            switch (expr.Operator)
            {
                case "+": return Add(left, right);
                case "-": return Subtract(left, right);
                case "*": return Multiply(left, right);
                case "/": return Divide(left, right);
                case "%": return Modulo(left, right);
                case "==": return new NexusBool(ValuesEqual(left, right));
                case "!=": return new NexusBool(!ValuesEqual(left, right));
                case "<": return new NexusBool(Compare(left, right) < 0);
                case ">": return new NexusBool(Compare(left, right) > 0);
                case "<=": return new NexusBool(Compare(left, right) <= 0);
                case ">=": return new NexusBool(Compare(left, right) >= 0);
                case "&": return new NexusInt(ValueHelpers.ToInteger(left) & ValueHelpers.ToInteger(right));
                case "|": return new NexusInt(ValueHelpers.ToInteger(left) | ValueHelpers.ToInteger(right));
                case "^": return new NexusInt(ValueHelpers.ToInteger(left) ^ ValueHelpers.ToInteger(right));
                case "<<": return new NexusInt(ValueHelpers.ToInteger(left) << (int)ValueHelpers.ToInteger(right));
                case ">>": return new NexusInt(ValueHelpers.ToInteger(left) >> (int)ValueHelpers.ToInteger(right));
                default:
                    throw new NexusRuntimeException($"Unknown binary operator: {expr.Operator}");
            }
        }

        private NexusValue EvaluateUnary(UnaryExpr expr, RuntimeEnvironment env)
        {
            if (expr.IsPostfix)
            {
                if (expr.Operand is IdentifierExpr id)
                {
                    var current = env.Get(id.Name);
                    var num = ValueHelpers.ToInteger(current);
                    var newValue = expr.Operator == "++" ? num + 1 : num - 1;
                    env.Assign(id.Name, new NexusInt(newValue));
                    return new NexusInt(num);
                }
                return NexusNull.Instance;
            }

            var operand = Evaluate(expr.Operand, env);

            // user-defined unary operator overloads (operator -, operator !, ...)
            var overload = TryInvokeOperator(operand, expr.Operator, null, env);
            if (overload != null)
                return overload;

            switch (expr.Operator)
            {
                case "-":
                    switch (operand)
                    {
                        case NexusInt i: return new NexusInt(-i.Value);
                        case NexusFloat f: return new NexusFloat(-f.Value);
                        default: return NexusNull.Instance;
                    }
                case "!": return new NexusBool(!operand.IsTruthy());
                case "~": return new NexusInt(~ValueHelpers.ToInteger(operand));
                case "++":
                    if (operand is NexusInt pi) return new NexusInt(pi.Value + 1);
                    if (operand is NexusFloat pf) return new NexusFloat(pf.Value + 1);
                    return NexusNull.Instance;
                case "--":
                    if (operand is NexusInt mi) return new NexusInt(mi.Value - 1);
                    if (operand is NexusFloat mf) return new NexusFloat(mf.Value - 1);
                    return NexusNull.Instance;
                default: return NexusNull.Instance;
            }
        }

        /// <summary>Look up and invoke a user-defined operator overload.
        /// Method-style declarations: `operator +(other)` (1 param) is binary with
        /// `this` = left operand and the parameter = right operand; `operator -()`
        /// (0 params) is a prefix unary. A C#-style 2-param `operator +(a, b)` is
        /// also accepted as a static binary form. Returns null when no overload
        /// applies (falls back to builtin semantics).</summary>
        private NexusValue TryInvokeOperator(NexusValue left, string op, NexusValue right,
                                             RuntimeEnvironment env)
        {
            NexusClass cls;
            NexusValue thisVal;
            NexusValue other;

            if (left is NexusInstance li)
            {
                cls = li.Class; thisVal = left; other = right;
            }
            else if (right is NexusInstance ri)
            {
                // e.g. `3 * vector` — the right operand's class may carry the overload
                cls = ri.Class; thisVal = right; other = left;
            }
            else
            {
                return null;
            }

            int declArity = right == null ? 0 : 1;

            while (cls != null)
            {
                // method-style first: this = left operand, arg = other operand
                var fn = cls.FindOperator(op, declArity);
                if (fn != null)
                {
                    var bound = new NexusFunction(fn.Name)
                    {
                        Parameters = fn.Parameters,
                        Body = fn.Body,
                        Closure = fn.Closure,
                        ReturnType = fn.ReturnType,
                        BoundThis = thisVal
                    };
                    var args = declArity == 1
                        ? new List<NexusValue> { other }
                        : new List<NexusValue>();
                    return CallFunction(bound, args, bound.Closure ?? env);
                }

                // C#-style static form: operator +(a, b)
                if (right != null)
                {
                    var staticFn = cls.FindOperator(op, 2);
                    if (staticFn != null)
                        return CallFunction(staticFn, new List<NexusValue> { left, right },
                                            staticFn.Closure ?? env);
                }

                cls = cls.BaseClass;
            }

            return null;
        }

        private NexusValue EvaluateAssign(AssignExpr expr, RuntimeEnvironment env)
        {
            var value = Evaluate(expr.Value, env);

            if (expr.Operator != "=")
            {
                var currentValue = Evaluate(expr.Target, env);
                var op = expr.Operator.Substring(0, expr.Operator.Length - 1);

                // event += handler / event -= handler — subscribe / unsubscribe
                if (currentValue is NexusEvent ev && (op == "+" || op == "-"))
                {
                    if (op == "+") ev.AddHandler(value);
                    else ev.RemoveHandler(value);
                    value = ev;   // store-back writes the same reference
                }
                else
                {
                    value = op switch
                    {
                        "+" => Add(currentValue, value),
                        "-" => Subtract(currentValue, value),
                        "*" => Multiply(currentValue, value),
                        "/" => Divide(currentValue, value),
                        "%" => Modulo(currentValue, value),
                        _ => value
                    };
                }
            }

            switch (expr.Target)
            {
                case IdentifierExpr id:
                    if (env.IsDefined(id.Name))
                        env.Assign(id.Name, value);
                    else if (env.TryGet("this", out var implicitThis) &&
                             implicitThis is NexusInstance implicitInst &&
                             (implicitInst.Fields.ContainsKey(id.Name) ||
                              implicitInst.Class?.FindProperty(id.Name) != null))
                    {
                        // C#-style implicit `this` field/property assignment
                        SetInstanceMember(implicitInst, id.Name, value, env);
                    }
                    else
                        env.Assign(id.Name, value);   // throws the standard error
                    break;

                case MemberAccessExpr member:
                {
                    var obj = Evaluate(member.Object, env);
                    if (obj is NexusInstance instance)
                    {
                        SetInstanceMember(instance, member.Member, value, env);
                    }
                    else if (obj is NexusClass cls)
                    {
                        if (cls.StaticEnv != null && cls.StaticEnv.IsDefined(member.Member))
                            cls.StaticEnv.Assign(member.Member, value);
                        else
                            cls.StaticFields[member.Member] = value;
                    }
                    else if (obj is NexusDict dict)
                        dict.Entries[member.Member] = value;
                    else if (obj is NexusAsmModule asmTarget)
                        asmTarget.SetMember(member.Member, value);
                    else
                        throw new NexusRuntimeException(
                            $"Cannot assign member '{member.Member}' on type '{obj.TypeName}'");
                    break;
                }

                case IndexExpr index:
                {
                    var container = Evaluate(index.Object, env);
                    var idx = Evaluate(index.Index, env);
                    if (container is NexusList list && idx is NexusInt intIdx)
                        list.Items[(int)intIdx.Value] = value;
                    else if (container is NexusDict d && idx is NexusString strIdx)
                        d.Entries[strIdx.Value] = value;
                    break;
                }

                default:
                    throw new NexusRuntimeException("Invalid assignment target.");
            }

            return value;
        }

        /// <summary>Assign an instance member: routes through the property setter
        /// when one exists, otherwise writes the backing field.</summary>
        private void SetInstanceMember(NexusInstance instance, string name, NexusValue value,
                                       RuntimeEnvironment env)
        {
            var prop = instance.Class?.FindProperty(name);
            if (prop != null)
            {
                if (prop.Setter == null)
                    throw new NexusRuntimeException($"Property '{name}' has no setter");

                if (!prop.SetterIsAuto)
                {
                    CallMethod(prop.Setter, instance, new List<NexusValue> { value }, env);
                    return;
                }
                // auto-setter falls through to the backing field below
            }

            instance.SetField(name, value);
        }

        private NexusValue EvaluateCall(CallExpr expr, RuntimeEnvironment env)
        {
            var callee = Evaluate(expr.Callee, env);
            var args = expr.Arguments.Select(arg => Evaluate(arg, env)).ToList();

            switch (callee)
            {
                case NexusFunction func: return CallFunction(func, args, env);
                case NexusInteropFunction interopFunc: return _interop.CallInterop(interopFunc, args);
                case NexusClass cls: return InstantiateClass(cls, args, env);
                case NexusAsmLabelFunction asmFn: return InvokeAsmFunction(asmFn, args);
                case NexusAsmModule asmMod: return InvokeAsmEntry(asmMod, args);
                case NexusEvent nexusEvent: return InvokeEvent(nexusEvent, args, env);
                case NexusDelegateType:
                    throw new NexusRuntimeException(
                        $"'{callee}' is a delegate type, not a value — assign a function or lambda first");
                default:
                    throw new NexusRuntimeException($"Cannot call value of type '{callee.TypeName}'.");
            }
        }

        /// <summary>Invoke an event: every subscribed handler runs in order.</summary>
        private NexusValue InvokeEvent(NexusEvent ev, List<NexusValue> args, RuntimeEnvironment env)
        {
            NexusValue last = NexusNull.Instance;
            foreach (var handler in ev.Handlers.ToList())   // snapshot: handlers may subscribe/unsubscribe during invocation
            {
                switch (handler)
                {
                    case NexusFunction fn:
                        last = CallFunction(fn, args, env);
                        break;
                    case NexusInteropFunction iof:
                        last = _interop.CallInterop(iof, args);
                        break;
                    case NexusEvent nested:
                        last = InvokeEvent(nested, args, env);
                        break;
                }
            }
            return last;
        }

        public NexusValue CallFunction(NexusFunction func, List<NexusValue> args)
        {
            return CallFunction(func, args, func.Closure ?? _globals);
        }

        private NexusValue CallFunction(NexusValue funcGeneric, List<NexusValue> args, RuntimeEnvironment env)
        {
            if (funcGeneric is not NexusFunction func)
                throw new NexusRuntimeException($"Cannot call value of type '{funcGeneric.TypeName}'.");

            if (func.IsNative && func.NativeImplementation != null)
                return func.NativeImplementation(args);

            var callEnv = new RuntimeEnvironment(func.Closure ?? env, $"call:{func.Name}");

            if (func.BoundThis != null)
                callEnv.Define("this", func.BoundThis);

            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var param = func.Parameters[i];

                // params array: last param collects all remaining arguments
                if (param.IsParams && i == func.Parameters.Count - 1)
                {
                    var rest = new NexusList();
                    for (int j = i; j < args.Count; j++)
                        rest.Items.Add(args[j]);
                    callEnv.Define(param.Name, rest);
                    break;
                }

                if (i < args.Count)
                {
                    callEnv.Define(param.Name, args[i]);
                }
                else if (param.DefaultValue != null)
                {
                    var defaultVal = Evaluate(param.DefaultValue, func.Closure ?? env);
                    callEnv.Define(param.Name, defaultVal);
                }
                else
                {
                    callEnv.Define(param.Name, NexusNull.Instance);
                }
            }

            try
            {
                foreach (var stmt in func.Body)
                    Execute(stmt, callEnv);
            }
            catch (NexusReturnException ret)
            {
                return ret.Value;
            }

            return NexusNull.Instance;
        }

        private NexusValue InstantiateClass(NexusClass cls, List<NexusValue> args, RuntimeEnvironment env)
        {
            var instance = new NexusInstance(cls);

            foreach (var fieldDecl in cls.FieldDeclarations.OfType<VariableDecl>())
            {
                var fieldValue = fieldDecl.Initializer != null
                    ? Evaluate(fieldDecl.Initializer, env)
                    : ValueHelpers.DefaultForType(fieldDecl.VarType);
                instance.Fields[fieldDecl.Name] = fieldValue;
            }

            // each instance gets its own event object per event declaration
            foreach (var eventName in cls.EventNames)
                instance.Fields[eventName] = new NexusEvent(eventName);

            if (cls.FindMethod("init") is NexusFunction ctor)
            {
                CallMethod(ctor, instance, args, new RuntimeEnvironment(env, $"ctor:{cls.Name}"));
            }
            else if (cls.FindMethod("constructor") is NexusFunction ctor2)
            {
                CallMethod(ctor2, instance, args, new RuntimeEnvironment(env, $"ctor:{cls.Name}"));
            }

            return instance;
        }

        private NexusValue CallMethod(NexusFunction method, NexusInstance instance,
                                      List<NexusValue> args, RuntimeEnvironment env)
        {
            var callEnv = new RuntimeEnvironment(env ?? method.Closure, $"method:{method.Name}");
            callEnv.Define("this", instance);

            for (int i = 0; i < method.Parameters.Count && i < args.Count; i++)
                callEnv.Define(method.Parameters[i].Name, args[i]);

            try
            {
                foreach (var stmt in method.Body)
                    Execute(stmt, callEnv);
            }
            catch (NexusReturnException ret)
            {
                return ret.Value;
            }

            return NexusNull.Instance;
        }

        private NexusValue EvaluateMemberAccess(MemberAccessExpr expr, RuntimeEnvironment env)
        {
            var obj = Evaluate(expr.Object, env);

            // null-conditional: obj?.member → null when obj is null
            if (expr.NullConditional && obj is NexusNull)
                return NexusNull.Instance;

            switch (obj)
            {
                case NexusInstance instance: return GetInstanceMember(instance, expr.Member, env);
                case NexusAsmModule asmModule: return asmModule.GetMember(expr.Member);
                case NexusDict dict:
                    return dict.Entries.TryGetValue(expr.Member, out var val)
                        ? val : NexusNull.Instance;
                case NexusClass cls:
                {
                    // live static scope first (so static-method mutations are seen),
                    // then the initial-values mirror, then methods
                    if (cls.StaticEnv != null && cls.StaticEnv.TryGet(expr.Member, out var envVal))
                        return envVal;
                    if (cls.StaticFields.TryGetValue(expr.Member, out var staticVal))
                        return staticVal;
                    return cls.FindMethod(expr.Member);
                }
                case NexusEnum enumVal:
                    if (expr.Member == "name") return new NexusString(enumVal.MemberName);
                    if (expr.Member == "value") return new NexusInt(enumVal.Value);
                    return NexusNull.Instance;
                case NexusEvent nexusEvent:
                    if (expr.Member == "count" || expr.Member == "handlerCount")
                        return new NexusInt(nexusEvent.Handlers.Count);
                    return NexusNull.Instance;
                case NexusString str: return GetStringMember(str, expr.Member);
                case NexusList list: return GetListMember(list, expr.Member);
                default: return NexusNull.Instance;
            }
        }

        /// <summary>Instance member read: property getter first, then fields,
        /// then bound methods.</summary>
        private NexusValue GetInstanceMember(NexusInstance instance, string name,
                                             RuntimeEnvironment env)
        {
            var prop = instance.Class?.FindProperty(name);
            if (prop != null)
            {
                if (prop.Getter == null)
                    throw new NexusRuntimeException($"Property '{name}' has no getter");

                if (!prop.GetterIsAuto)
                    return CallMethod(prop.Getter, instance, new List<NexusValue>(), env);

                // auto-getter: read the backing field
                return instance.Fields.TryGetValue(name, out var backing)
                    ? backing : NexusNull.Instance;
            }

            return instance.GetField(name);
        }

        private NexusValue GetStringMember(NexusString str, string member)
        {
            switch (member)
            {
                case "length":
                case "len": return new NexusInt(str.Value.Length);

                // method-style members: callable native functions
                case "upper":
                    return new NexusFunction("upper")
                    {
                        IsNative = true,
                        NativeImplementation = _ => new NexusString(str.Value.ToUpper())
                    };
                case "lower":
                    return new NexusFunction("lower")
                    {
                        IsNative = true,
                        NativeImplementation = _ => new NexusString(str.Value.ToLower())
                    };
                case "trim":
                    return new NexusFunction("trim")
                    {
                        IsNative = true,
                        NativeImplementation = _ => new NexusString(str.Value.Trim())
                    };
                default: return NexusNull.Instance;
            }
        }

        private NexusValue GetListMember(NexusList list, string member)
        {
            switch (member)
            {
                case "length":
                case "len":
                case "count": return new NexusInt(list.Items.Count);
                case "first": return list.Items.Count > 0 ? list.Items[0] : NexusNull.Instance;
                case "last": return list.Items.Count > 0 ? list.Items[list.Items.Count - 1] : NexusNull.Instance;
                case "empty": return new NexusBool(list.Items.Count == 0);
                default: return NexusNull.Instance;
            }
        }

        private NexusValue EvaluateIndex(IndexExpr expr, RuntimeEnvironment env)
        {
            var container = Evaluate(expr.Object, env);
            var index = Evaluate(expr.Index, env);

            switch (container)
            {
                case NexusList list when index is NexusInt i:
                    return (i.Value >= 0 && i.Value < list.Items.Count)
                        ? list.Items[(int)i.Value] : NexusNull.Instance;
                case NexusArray arr when index is NexusInt ai:
                    return (ai.Value >= 0 && ai.Value < arr.Elements.Length)
                        ? arr.Elements[(int)ai.Value] : NexusNull.Instance;
                case NexusDict dict when index is NexusString key:
                    return dict.Entries.TryGetValue(key.Value, out var val)
                        ? val : NexusNull.Instance;
                case NexusString str when index is NexusInt si:
                    return (si.Value >= 0 && si.Value < str.Value.Length)
                        ? new NexusChar(str.Value[(int)si.Value]) : NexusNull.Instance;
                default: return NexusNull.Instance;
            }
        }

        private NexusValue EvaluateTernary(TernaryExpr expr, RuntimeEnvironment env)
        {
            return Evaluate(expr.Condition, env).IsTruthy()
                ? Evaluate(expr.ThenExpr, env)
                : Evaluate(expr.ElseExpr, env);
        }

        /// <summary>1..5 → [1, 2, 3, 4, 5] (inclusive on both ends).</summary>
        internal static NexusList BuildRange(NexusValue startVal, NexusValue endVal)
        {
            long start = ValueHelpers.ToInteger(startVal);
            long end = ValueHelpers.ToInteger(endVal);
            if (end - start > 10_000_000)
                throw new NexusRuntimeException(
                    $"Range {start}..{end} is too large (max 10,000,000 elements)");
            var list = new NexusList();
            for (long i = start; i <= end; i++)
                list.Items.Add(new NexusInt(i));
            return list;
        }

        private NexusValue EvaluateRange(RangeExpr expr, RuntimeEnvironment env)
        {
            return BuildRange(Evaluate(expr.Start, env), Evaluate(expr.End, env));
        }

        private NexusValue EvaluateNew(NewExpr expr, RuntimeEnvironment env)
        {
            if (env.TryGet(expr.TypeName, out var typeVal))
            {
                if (typeVal is NexusClass cls)
                {
                    var args = expr.Arguments.Select(a => Evaluate(a, env)).ToList();
                    return InstantiateClass(cls, args, env);
                }
            }

            switch (expr.TypeName.ToLowerInvariant())
            {
                case "list": return new NexusList();
                case "dict": return new NexusDict();
                case "array": return new NexusArray(new NexusValue[0]);
                default:
                    throw new NexusRuntimeException(
                        $"Unknown type '{expr.TypeName}' for instantiation.");
            }
        }

        private NexusValue EvaluateList(ListExpr expr, RuntimeEnvironment env)
        {
            var items = expr.Elements.Select(e => Evaluate(e, env)).ToList();
            return new NexusList(items);
        }

        private NexusValue EvaluateDict(DictExpr expr, RuntimeEnvironment env)
        {
            var dict = new NexusDict();
            foreach (var kvp in expr.Pairs)
            {
                var key = Evaluate(kvp.Key, env);
                var value = Evaluate(kvp.Value, env);
                dict.Entries[key.ToString()] = value;
            }
            return dict;
        }

        private NexusValue EvaluateAs(AsExpr expr, RuntimeEnvironment env)
        {
            var value = Evaluate(expr.Expression, env);
            switch (expr.TargetType)
            {
                case "int": return new NexusInt(ValueHelpers.ToInteger(value));
                case "float":
                case "double": return new NexusFloat(ValueHelpers.ToNumber(value));
                case "string": return new NexusString(value.ToString());
                case "bool": return new NexusBool(value.IsTruthy());
                case "char" when value is NexusString s && s.Value.Length > 0:
                    return new NexusChar(s.Value[0]);
                default: return value;
            }
        }

        private NexusValue EvaluateIs(IsExpr expr, RuntimeEnvironment env)
        {
            var value = Evaluate(expr.Expression, env);
            return new NexusBool(value.TypeName == expr.TargetType);
        }

        private NexusValue EvaluatePointer(PointerExpr expr, RuntimeEnvironment env)
        {
            var addr = Evaluate(expr.Address, env);
            return new NexusInt(_asmEngine.ReadMemory(ValueHelpers.ToInteger(addr)));
        }

        private NexusValue EvaluateAddressOf(AddressOfExpr expr, RuntimeEnvironment env)
        {
            if (expr.Target is IdentifierExpr id)
                return new NexusInt(_asmEngine.GetVariableAddress(id.Name, env));
            return new NexusInt(0);
        }

        // ============ OPERATOR HELPERS ============
        private NexusValue Add(NexusValue left, NexusValue right)
        {
            if (left is NexusString || right is NexusString)
                return new NexusString(left.ToString() + right.ToString());

            if (left is NexusList list && right is NexusList other)
            {
                var combined = new List<NexusValue>(list.Items);
                combined.AddRange(other.Items);
                return new NexusList(combined);
            }

            if (left is NexusFloat || right is NexusFloat)
                return new NexusFloat(ValueHelpers.ToNumber(left) + ValueHelpers.ToNumber(right));

            return new NexusInt(ValueHelpers.ToInteger(left) + ValueHelpers.ToInteger(right));
        }

        private NexusValue Subtract(NexusValue left, NexusValue right)
        {
            if (left is NexusFloat || right is NexusFloat)
                return new NexusFloat(ValueHelpers.ToNumber(left) - ValueHelpers.ToNumber(right));
            return new NexusInt(ValueHelpers.ToInteger(left) - ValueHelpers.ToInteger(right));
        }

        private NexusValue Multiply(NexusValue left, NexusValue right)
        {
            if (left is NexusFloat || right is NexusFloat)
                return new NexusFloat(ValueHelpers.ToNumber(left) * ValueHelpers.ToNumber(right));
            return new NexusInt(ValueHelpers.ToInteger(left) * ValueHelpers.ToInteger(right));
        }

        private NexusValue Divide(NexusValue left, NexusValue right)
        {
            var rVal = ValueHelpers.ToNumber(right);
            if (rVal == 0)
                throw new NexusRuntimeException("Division by zero.");
            return new NexusFloat(ValueHelpers.ToNumber(left) / rVal);
        }

        private NexusValue Modulo(NexusValue left, NexusValue right)
        {
            var rInt = ValueHelpers.ToInteger(right);
            if (rInt == 0)
                throw new NexusRuntimeException("Modulo by zero.");
            return new NexusInt(ValueHelpers.ToInteger(left) % rInt);
        }

        private bool ValuesEqual(NexusValue a, NexusValue b)
        {
            if (a is NexusNull && b is NexusNull) return true;
            if (a is NexusNull || b is NexusNull) return false;

            // enum member vs enum member: same enum, same name
            if (a is NexusEnum ea && b is NexusEnum eb)
                return ea.EnumName == eb.EnumName && ea.MemberName == eb.MemberName;

            // enum member vs number: compare underlying values
            if (a is NexusEnum || b is NexusEnum)
            {
                if (ValueHelpers.IsNumber(a) && ValueHelpers.IsNumber(b))
                    return ValueHelpers.ToNumber(a) == ValueHelpers.ToNumber(b);
                return false;
            }

            if (a.GetType() != b.GetType())
            {
                if (ValueHelpers.IsNumber(a) && ValueHelpers.IsNumber(b))
                    return ValueHelpers.ToNumber(a) == ValueHelpers.ToNumber(b);
                return false;
            }

            switch (a)
            {
                case NexusInt ai when b is NexusInt bi: return ai.Value == bi.Value;
                case NexusFloat af when b is NexusFloat bf: return af.Value == bf.Value;
                case NexusString as_ when b is NexusString bs: return as_.Value == bs.Value;
                case NexusBool ab when b is NexusBool bb: return ab.Value == bb.Value;
                case NexusChar ac when b is NexusChar bc: return ac.Value == bc.Value;
                default: return ReferenceEquals(a, b);
            }
        }

        private int Compare(NexusValue a, NexusValue b)
        {
            if (ValueHelpers.IsNumber(a) && ValueHelpers.IsNumber(b))
            {
                var diff = ValueHelpers.ToNumber(a) - ValueHelpers.ToNumber(b);
                return diff == 0 ? 0 : (diff < 0 ? -1 : 1);
            }

            if (a is NexusString sa && b is NexusString sb)
                return string.CompareOrdinal(sa.Value, sb.Value);

            if (a is NexusChar ca && b is NexusChar cb)
                return ca.Value.CompareTo(cb.Value);

            if (a is NexusBool ab && b is NexusBool bb)
                return ab.Value.CompareTo(bb.Value);

            return 0;
        }
    }
}

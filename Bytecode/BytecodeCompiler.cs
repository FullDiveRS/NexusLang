using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Core;
using Nexus.Interop;
using Nexus.Runtime;

namespace Nexus.Bytecode
{
    /// <summary>Compiles the Nexus AST to stack-based bytecode.</summary>
    public class BytecodeCompiler
    {
        private readonly List<string> _errors = new List<string>();
        private BytecodeChunk _currentChunk;
        private readonly Dictionary<string, BytecodeChunk> _functions = new Dictionary<string, BytecodeChunk>();
        private readonly Dictionary<string, int> _variableMap = new Dictionary<string, int>();
        private int _nextVarIndex;
        private readonly List<int> _breakPositions = new List<int>();
        private readonly List<int> _continuePositions = new List<int>();
        private int _lambdaCounter;
        private int _foreachDepth;
        private int _switchCounter;
        private int _setTmpCounter;
        private int _tryIdCounter;

        /// <summary>Open try scopes while compiling — break/continue/return inside
        /// a try must emit TRY_POP so handlers don't leak.</summary>
        private readonly List<int> _tryIds = new List<int>();

        /// <summary>Base class of the class being compiled (for CALL_SUPER).</summary>
        private string _currentBaseClassName;

        /// <summary>While compiling class/enum member bodies: bare references to
        /// static members rewrite to `<container>.<member>` (LOAD container;
        /// GET_MEMBER) so the flat-variable VM sees live static state.</summary>
        private readonly Dictionary<string, Tuple<string, string>> _staticsMap =
            new Dictionary<string, Tuple<string, string>>();

        /// <summary>While compiling class/enum member bodies: bare sibling static
        /// method calls rewrite to their qualified chunk names.</summary>
        private readonly Dictionary<string, string> _callNameMap = new Dictionary<string, string>();

        /// <summary>Instance field names of the class being compiled. Bare reads
        /// and writes of these names inside instance member bodies rewrite to
        /// `this.member` (C#-style implicit this, mirroring the interpreter).
        /// Null while compiling anything that has no `this`.</summary>
        private HashSet<string> _compilingClassFields;
        private HashSet<string> _currentInstanceFields;

        /// <summary>Names declared as params/locals in the member function being
        /// compiled — they shadow instance fields and must NOT be rewritten.</summary>
        private readonly HashSet<string> _declaredLocals = new HashSet<string>();

        public List<string> Errors => _errors;

        public Dictionary<string, BytecodeChunk> Compile(ProgramNode program)
        {
            var mainChunk = new BytecodeChunk { Name = "__main__" };
            _currentChunk = mainChunk;
            _variableMap.Clear();
            _nextVarIndex = 0;

            foreach (var stmt in program.Statements)
                CompileStatement(stmt);

            _currentChunk.AddInstruction(new Instruction(OpCode.HALT));
            _functions["__main__"] = mainChunk;

            return _functions;
        }

        private void CompileStatement(Statement stmt)
        {
            switch (stmt)
            {
                case VariableDecl vd:
                    CompileVariableDecl(vd);
                    break;

                case MultiVariableDecl mvd:
                    foreach (var d in mvd.Declarations)
                        CompileVariableDecl(d);
                    break;

                case ExpressionStmt es:
                    CompileExpression(es.Expression);
                    _currentChunk.AddInstruction(new Instruction(OpCode.POP));
                    break;

                case IfStmt ifs:
                    CompileIf(ifs);
                    break;

                case WhileStmt ws:
                    CompileWhile(ws);
                    break;

                case ForStmt fs:
                    CompileFor(fs);
                    break;

                case FunctionDecl fd:
                    CompileFunction(fd);
                    break;

                case ReturnStmt rs:
                    if (rs.Value != null)
                        CompileExpression(rs.Value);
                    else
                        _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    EmitTryExits();   // leaving the try scope — release handlers
                    _currentChunk.AddInstruction(new Instruction(OpCode.RETURN));
                    break;

                case BlockStmt bs:
                    foreach (var s in bs.Statements)
                        CompileStatement(s);
                    break;

                case ClassDecl cd:
                    CompileClass(cd);
                    break;

                case EnumDecl ed:
                    CompileEnum(ed);
                    break;

                case DelegateDecl dd:
                {
                    var delType = new NexusDelegateType(dd.Name) { ReturnType = dd.ReturnType };
                    foreach (var p in dd.Parameters)
                        delType.Parameters.Add(p);
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant(delType)));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.STORE_VAR, GetOrAddVariable(dd.Name)));
                    break;
                }

                case EventDecl ed:
                {
                    var evConst = new NexusEvent(ed.Name, ed.DelegateType);
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant(evConst)));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.STORE_VAR, GetOrAddVariable(ed.Name)));
                    break;
                }

                case PropertyDecl pd:
                    _errors.Add($"Bytecode: top-level property '{pd.Name}' is only allowed inside a class");
                    break;

                case ForeachStmt fs:
                    CompileForeach(fs);
                    break;

                case AsmBlock ab:
                    CompileAsmBlock(ab);
                    break;

                case PrintStmt ps:
                    foreach (var value in ps.Values)
                        CompileExpression(value);
                    _currentChunk.AddInstruction(new Instruction(
                        ps.Newline ? OpCode.PRINTLN : OpCode.PRINT,
                        ps.Values.Count)); // operand = how many values to pop & join
                    break;

                case InteropDeclaration id:
                    CompileInteropDeclaration(id);
                    break;

                case SwitchStmt ss:
                    CompileSwitch(ss);
                    break;

                case TryCatchStmt tc:
                    CompileTryCatch(tc);
                    break;

                case ThrowStmt ts:
                    if (ts.Value != null)
                        CompileExpression(ts.Value);
                    else
                        _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    _currentChunk.AddInstruction(new Instruction(OpCode.THROW));
                    break;

                case BreakStmt:
                    EmitTryExits();
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP, -1));
                    _breakPositions.Add(_currentChunk.Instructions.Count - 1);
                    break;

                case ContinueStmt:
                    EmitTryExits();
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP, -1));
                    _continuePositions.Add(_currentChunk.Instructions.Count - 1);
                    break;

                default:
                    _errors.Add($"Bytecode: unhandled statement '{stmt.GetType().Name}' — skipped");
                    break;
            }
        }

        private void CompileVariableDecl(VariableDecl decl)
        {
            if (decl.Initializer != null)
                CompileExpression(decl.Initializer);
            else
                EmitTypedDefault(decl.VarType);   // `int x;` → 0, `bool b;` → false

            // a local declaration shadows any instance field of the same name
            _declaredLocals.Add(decl.Name);

            int varIndex = GetOrAddVariable(decl.Name);
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, varIndex));
        }

        private void EmitTypedDefault(string type)
        {
            switch (type)
            {
                case "int": case "short": case "long": case "byte":
                case "uint": case "ulong":
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_INT, 0L));
                    break;
                case "float": case "double":
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant(0.0)));
                    break;
                case "bool": case "boolean":
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_FALSE));
                    break;
                case "string":
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant("")));
                    break;
                default:
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    break;
            }
        }

        private void CompileIf(IfStmt stmt)
        {
            CompileExpression(stmt.Condition);
            int jumpIfFalse = _currentChunk.Instructions.Count;
            _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));

            CompileStatement(stmt.ThenBranch);

            if (stmt.ElseBranch != null)
            {
                int jumpElse = _currentChunk.Instructions.Count;
                _currentChunk.AddInstruction(new Instruction(OpCode.JMP, -1));

                int elseStart = _currentChunk.Instructions.Count;
                _currentChunk.Instructions[jumpIfFalse].Operand1 = elseStart;

                CompileStatement(stmt.ElseBranch);

                int endIf = _currentChunk.Instructions.Count;
                _currentChunk.Instructions[jumpElse].Operand1 = endIf;
            }
            else
            {
                int endIf = _currentChunk.Instructions.Count;
                _currentChunk.Instructions[jumpIfFalse].Operand1 = endIf;
            }
        }

        private void CompileWhile(WhileStmt stmt)
        {
            int loopStart = _currentChunk.Instructions.Count;

            CompileExpression(stmt.Condition);
            int jumpIfFalse = _currentChunk.Instructions.Count;
            _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));

            var savedBreaks = new List<int>(_breakPositions);
            var savedContinues = new List<int>(_continuePositions);
            _breakPositions.Clear();
            _continuePositions.Clear();

            CompileStatement(stmt.Body);

            foreach (var pos in _continuePositions)
                _currentChunk.Instructions[pos].Operand1 = loopStart;

            _currentChunk.AddInstruction(new Instruction(OpCode.JMP, loopStart));

            int loopEnd = _currentChunk.Instructions.Count;
            _currentChunk.Instructions[jumpIfFalse].Operand1 = loopEnd;

            foreach (var pos in _breakPositions)
                _currentChunk.Instructions[pos].Operand1 = loopEnd;

            _breakPositions.Clear();
            _breakPositions.AddRange(savedBreaks);
            _continuePositions.Clear();
            _continuePositions.AddRange(savedContinues);
        }

        private void CompileFor(ForStmt stmt)
        {
            if (stmt.Initializer != null)
                CompileStatement(stmt.Initializer);

            int loopStart = _currentChunk.Instructions.Count;

            if (stmt.Condition != null)
            {
                CompileExpression(stmt.Condition);
                int jumpIfFalse = _currentChunk.Instructions.Count;
                _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));

                var savedBreaks = new List<int>(_breakPositions);
                var savedContinues = new List<int>(_continuePositions);
                _breakPositions.Clear();
                _continuePositions.Clear();

                CompileStatement(stmt.Body);

                int continueTarget = _currentChunk.Instructions.Count;
                foreach (var pos in _continuePositions)
                    _currentChunk.Instructions[pos].Operand1 = continueTarget;

                if (stmt.Increment != null)
                {
                    CompileExpression(stmt.Increment);
                    // the increment's value is discarded — keep the stack balanced
                    _currentChunk.AddInstruction(new Instruction(OpCode.POP));
                }

                _currentChunk.AddInstruction(new Instruction(OpCode.JMP, loopStart));

                int loopEnd = _currentChunk.Instructions.Count;
                _currentChunk.Instructions[jumpIfFalse].Operand1 = loopEnd;

                foreach (var pos in _breakPositions)
                    _currentChunk.Instructions[pos].Operand1 = loopEnd;

                _breakPositions.Clear();
                _breakPositions.AddRange(savedBreaks);
                _continuePositions.Clear();
                _continuePositions.AddRange(savedContinues);
            }
            else
            {
                var savedBreaks = new List<int>(_breakPositions);
                _breakPositions.Clear();

                CompileStatement(stmt.Body);

                if (stmt.Increment != null)
                {
                    CompileExpression(stmt.Increment);
                    _currentChunk.AddInstruction(new Instruction(OpCode.POP));
                }

                _currentChunk.AddInstruction(new Instruction(OpCode.JMP, loopStart));

                foreach (var pos in _breakPositions)
                    _currentChunk.Instructions[pos].Operand1 = _currentChunk.Instructions.Count;

                _breakPositions.Clear();
                _breakPositions.AddRange(savedBreaks);
            }
        }

        private void CompileFunction(FunctionDecl decl)
        {
            var funcChunk = new BytecodeChunk { Name = decl.Name };
            var savedChunk = _currentChunk;
            _currentChunk = funcChunk;

            foreach (var param in decl.Parameters)
                funcChunk.ParamSlots.Add(GetOrAddVariable(param.Name));

            // Default-argument prologue: if a param slot is still null at entry
            // (caller didn't pass it), store its default value.
            for (int pi = 0; pi < decl.Parameters.Count; pi++)
            {
                var param = decl.Parameters[pi];
                if (param.DefaultValue == null) continue;

                int vi = GetOrAddVariable(param.Name);
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, vi));
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                _currentChunk.AddInstruction(new Instruction(OpCode.EQ));
                int jmp = _currentChunk.Instructions.Count;
                _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));
                CompileExpression(param.DefaultValue);
                _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, vi));
                _currentChunk.Instructions[jmp].Operand1 = _currentChunk.Instructions.Count;
            }

            foreach (var bodyStmt in decl.Body)
                CompileStatement(bodyStmt);

            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
            _currentChunk.AddInstruction(new Instruction(OpCode.RETURN));

            _currentChunk = savedChunk;
            _functions[decl.Name] = funcChunk;

            // Bind the name so runtime CALL dispatch can find it:
            //   LOAD_CONST "name" ; STORE_VAR slot
            int nameIdx = _currentChunk.AddConstant(decl.Name);
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_CONST, nameIdx));
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(decl.Name)));
        }

        /// <summary>Compile a method / accessor / operator into a named chunk.
        /// Unlike top-level functions there is no name-binding tail — these are
        /// dispatched through class metadata or rewrites. Instance members get a
        /// `this` slot the VM fills from the bound receiver.</summary>
        private void CompileMemberFunction(FunctionDecl decl, string chunkName,
                                           bool hasThis)
        {
            var funcChunk = new BytecodeChunk { Name = chunkName };
            if (hasThis)
                funcChunk.ThisSlot = GetOrAddVariable("this");

            foreach (var param in decl.Parameters)
                funcChunk.ParamSlots.Add(GetOrAddVariable(param.Name));

            var savedChunk = _currentChunk;
            var savedInstFields = _currentInstanceFields;
            var savedLocals = new HashSet<string>(_declaredLocals);
            _currentInstanceFields = hasThis ? _compilingClassFields : null;
            _declaredLocals.Clear();
            foreach (var param in decl.Parameters)
                _declaredLocals.Add(param.Name);
            _currentChunk = funcChunk;

            for (int pi = 0; pi < decl.Parameters.Count; pi++)
            {
                var param = decl.Parameters[pi];
                if (param.DefaultValue == null) continue;

                int vi = GetOrAddVariable(param.Name);
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, vi));
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                _currentChunk.AddInstruction(new Instruction(OpCode.EQ));
                int jmp = _currentChunk.Instructions.Count;
                _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));
                CompileExpression(param.DefaultValue);
                _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, vi));
                _currentChunk.Instructions[jmp].Operand1 = _currentChunk.Instructions.Count;
            }

            foreach (var bodyStmt in decl.Body)
                CompileStatement(bodyStmt);

            _currentInstanceFields = savedInstFields;
            _declaredLocals.Clear();
            foreach (var name in savedLocals)
                _declaredLocals.Add(name);

            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
            _currentChunk.AddInstruction(new Instruction(OpCode.RETURN));

            _currentChunk = savedChunk;
            _functions[chunkName] = funcChunk;
        }

        private void CompileClass(ClassDecl decl)
        {
            var meta = new NexusClass(decl.Name);
            var savedBase = _currentBaseClassName;
            _currentBaseClassName = decl.BaseClass;

            // link to the base class metadata (compiled earlier) so inherited
            // methods / properties / operators resolve at runtime
            if (decl.BaseClass != null &&
                _compiledClasses.TryGetValue(decl.BaseClass, out var baseMeta))
                meta.BaseClass = baseMeta;
            var methodDecls = new List<FunctionDecl>();
            var operatorDecls = new List<FunctionDecl>();
            var staticFields = new List<VariableDecl>();
            var instanceFields = new List<VariableDecl>();
            var properties = new List<PropertyDecl>();
            var staticNames = new HashSet<string>();

            // ---- pass 1: collect members & build runtime class metadata ----
            foreach (var member in decl.Members)
            {
                switch (member)
                {
                    case FunctionDecl fd when fd.IsOperator:
                    {
                        var key = $"{fd.OperatorSymbol}#{fd.Parameters.Count}";
                        operatorDecls.Add(fd);
                        meta.Operators[key] = new NexusFunction($"{decl.Name}.op {key}");
                        break;
                    }
                    case FunctionDecl fd:
                        methodDecls.Add(fd);
                        meta.Methods[fd.Name] = new NexusFunction($"{decl.Name}.{fd.Name}");
                        if (fd.IsStatic) staticNames.Add(fd.Name);
                        break;
                    case PropertyDecl pd:
                        properties.Add(pd);
                        RegisterPropertyMeta(meta, decl.Name, pd);
                        break;
                    case EventDecl ed when ed.IsStatic:
                        meta.StaticFields[ed.Name] = new NexusEvent(ed.Name, ed.DelegateType);
                        staticNames.Add(ed.Name);
                        break;
                    case EventDecl ed:
                        meta.EventNames.Add(ed.Name);   // per-instance at construction
                        break;
                    case VariableDecl vd when vd.IsStatic:
                        staticFields.Add(vd);
                        staticNames.Add(vd.Name);
                        break;
                    case VariableDecl vd:
                        instanceFields.Add(vd);
                        meta.FieldDeclarations.Add(vd);
                        break;
                }
            }

            // ---- name rewrites used while compiling member bodies ----
            foreach (var fd in methodDecls)
                if (fd.IsStatic) _callNameMap[fd.Name] = $"{decl.Name}.{fd.Name}";
            foreach (var name in staticNames)
                _staticsMap[name] = Tuple.Create(decl.Name, name);

            // ---- pass 2: emit ----
            // CLASS_DEFINE pushes the (pre-built) class; STORE_VAR binds its name
            int metaIdx = _currentChunk.AddConstant(meta);
            _currentChunk.AddInstruction(new Instruction(OpCode.CLASS_DEFINE, metaIdx));
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR,
                GetOrAddVariable(decl.Name)));

            // static field initializers → SET_MEMBER on the class value
            foreach (var vd in staticFields)
            {
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR,
                    GetOrAddVariable(decl.Name)));
                if (vd.Initializer != null) CompileExpression(vd.Initializer);
                else EmitTypedDefault(vd.VarType);
                _currentChunk.AddInstruction(new Instruction(
                    OpCode.SET_MEMBER, _currentChunk.AddConstant(vd.Name)));
            }

            // instance field initializers → __fields_<cls> chunk returning a value list
            if (instanceFields.Count > 0)
            {
                var fieldsChunk = new BytecodeChunk { Name = $"__fields_{decl.Name}" };
                var savedChunk = _currentChunk;
                _currentChunk = fieldsChunk;
                _currentChunk.AddInstruction(new Instruction(OpCode.NEW_LIST));
                foreach (var vd in instanceFields)
                {
                    if (vd.Initializer != null) CompileExpression(vd.Initializer);
                    else EmitTypedDefault(vd.VarType);
                    _currentChunk.AddInstruction(new Instruction(OpCode.LIST_APPEND));
                }
                _currentChunk.AddInstruction(new Instruction(OpCode.RETURN));
                _currentChunk = savedChunk;
                _functions[$"__fields_{decl.Name}"] = fieldsChunk;
            }

            // methods / operators / property accessors
            _compilingClassFields = new HashSet<string>(instanceFields.Select(v => v.Name));
            foreach (var fd in methodDecls)
                CompileMemberFunction(fd, $"{decl.Name}.{fd.Name}", hasThis: !fd.IsStatic);
            foreach (var od in operatorDecls)
                CompileMemberFunction(od, $"{decl.Name}.op {od.OperatorSymbol}#{od.Parameters.Count}",
                                      hasThis: od.Parameters.Count <= 1);
            foreach (var pd in properties)
            {
                if (pd.GetterBody != null)
                    CompileMemberFunction(pd.GetterBody, $"{decl.Name}.get_{pd.Name}",
                                          hasThis: !pd.IsStatic);
                if (pd.SetterBody != null)
                    CompileMemberFunction(pd.SetterBody, $"{decl.Name}.set_{pd.Name}",
                                          hasThis: !pd.IsStatic);
            }

            // ---- clear rewrites ----
            foreach (var fd in methodDecls)
                if (fd.IsStatic) _callNameMap.Remove(fd.Name);
            foreach (var name in staticNames)
                _staticsMap.Remove(name);
            _compilingClassFields = null;

            _currentBaseClassName = savedBase;
            _compiledClasses[decl.Name] = meta;
        }

        private readonly Dictionary<string, NexusClass> _compiledClasses =
            new Dictionary<string, NexusClass>();

        private void RegisterPropertyMeta(NexusClass meta, string className, PropertyDecl pd)
        {
            var accessors = new PropertyAccessors { Name = pd.Name };
            if (pd.HasGetter)
            {
                accessors.GetterIsAuto = pd.GetterBody == null;
                accessors.Getter = new NexusFunction($"{className}.get_{pd.Name}");
            }
            if (pd.HasSetter)
            {
                accessors.SetterIsAuto = pd.SetterBody == null;
                accessors.Setter = new NexusFunction($"{className}.set_{pd.Name}");
            }
            meta.Properties[pd.Name] = accessors;
        }

        private void CompileEnum(EnumDecl decl)
        {
            // Enums compile to a pre-built table (dict): members are NexusEnum
            // values, methods are placeholder functions pointing at qualified
            // chunks, static fields are set at runtime via SET_MEMBER.
            var enumTable = new NexusDict();
            var staticNames = new HashSet<string>();
            var methodDecls = new List<FunctionDecl>();
            var staticFields = new List<VariableDecl>();

            foreach (var m in decl.Members)
            {
                enumTable.Entries[m.Item1] = new NexusEnum(decl.Name, m.Item1, m.Item2);
                staticNames.Add(m.Item1);
            }

            foreach (var member in decl.MemberStatements)
            {
                switch (member)
                {
                    case FunctionDecl fd when fd.IsOperator:
                        _errors.Add($"Bytecode: enum operator '{decl.Name}.operator {fd.OperatorSymbol}' is interpreter-only");
                        break;
                    case FunctionDecl fd:
                        methodDecls.Add(fd);
                        enumTable.Entries[fd.Name] = new NexusFunction($"{decl.Name}.{fd.Name}");
                        staticNames.Add(fd.Name);
                        break;
                    case VariableDecl vd:
                        staticFields.Add(vd);
                        staticNames.Add(vd.Name);
                        break;
                    case EventDecl ed:
                        enumTable.Entries[ed.Name] = new NexusEvent(ed.Name, ed.DelegateType);
                        staticNames.Add(ed.Name);
                        break;
                    case PropertyDecl pd:
                        _errors.Add($"Bytecode: enum property '{decl.Name}.{pd.Name}' is interpreter-only");
                        break;
                }
            }

            foreach (var fd in methodDecls)
                _callNameMap[fd.Name] = $"{decl.Name}.{fd.Name}";
            foreach (var name in staticNames)
                _staticsMap[name] = Tuple.Create(decl.Name, name);

            // store the table under the enum name
            _currentChunk.AddInstruction(new Instruction(
                OpCode.LOAD_CONST, _currentChunk.AddConstant(enumTable)));
            _currentChunk.AddInstruction(new Instruction(
                OpCode.STORE_VAR, GetOrAddVariable(decl.Name)));

            // static field initializers
            foreach (var vd in staticFields)
            {
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR,
                    GetOrAddVariable(decl.Name)));
                if (vd.Initializer != null) CompileExpression(vd.Initializer);
                else EmitTypedDefault(vd.VarType);
                _currentChunk.AddInstruction(new Instruction(
                    OpCode.SET_MEMBER, _currentChunk.AddConstant(vd.Name)));
            }

            foreach (var fd in methodDecls)
                CompileMemberFunction(fd, $"{decl.Name}.{fd.Name}", hasThis: false);

            foreach (var fd in methodDecls)
                _callNameMap.Remove(fd.Name);
            foreach (var name in staticNames)
                _staticsMap.Remove(name);
        }

        private void CompileForeach(ForeachStmt fs)
        {
            string src = $"__iter_src_{_foreachDepth}";
            string idx = $"__iter_idx_{_foreachDepth}";
            string len = $"__iter_len_{_foreachDepth}";
            _foreachDepth++;

            _declaredLocals.Add(fs.VariableName);
            CompileExpression(fs.Iterable);
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(src)));
            // dicts → key list; other iterables pass through unchanged
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(src)));
            _currentChunk.AddInstruction(new Instruction(OpCode.DICT_KEYS));
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(src)));

            // i = 0
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_INT, 0L));
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(idx)));

            // len = len(src)
            _currentChunk.AddInstruction(new Instruction(
                OpCode.LOAD_CONST, _currentChunk.AddConstant("len")));
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(src)));
            _currentChunk.AddInstruction(new Instruction(OpCode.CALL, 1));
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(len)));

            int loopStart = _currentChunk.Instructions.Count;
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(idx)));
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(len)));
            _currentChunk.AddInstruction(new Instruction(OpCode.LT));
            int exitJump = _currentChunk.Instructions.Count;
            _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));

            var savedBreaks = new List<int>(_breakPositions);
            var savedContinues = new List<int>(_continuePositions);
            _breakPositions.Clear();
            _continuePositions.Clear();

            // varName = src[i]
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(src)));
            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(idx)));
            _currentChunk.AddInstruction(new Instruction(OpCode.GET_INDEX));
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(fs.VariableName)));

            CompileStatement(fs.Body);

            int continueTarget = _currentChunk.Instructions.Count;
            foreach (var pos in _continuePositions)
                _currentChunk.Instructions[pos].Operand1 = continueTarget;

            _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(idx)));
            _currentChunk.AddInstruction(new Instruction(OpCode.INC));
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(idx)));
            _currentChunk.AddInstruction(new Instruction(OpCode.JMP, loopStart));

            int loopEnd = _currentChunk.Instructions.Count;
            _currentChunk.Instructions[exitJump].Operand1 = loopEnd;
            foreach (var pos in _breakPositions)
                _currentChunk.Instructions[pos].Operand1 = loopEnd;

            _breakPositions.Clear();
            _breakPositions.AddRange(savedBreaks);
            _continuePositions.Clear();
            _continuePositions.AddRange(savedContinues);
            _foreachDepth--;
        }

        /// <summary>switch compiles to an EQ/branch chain over a temp slot.
        /// No fall-through: each case ends with a JMP to the switch end; a
        /// `break` inside a case also targets the switch end (matching the
        /// interpreter's per-case break semantics, NOT the enclosing loop).</summary>
        private void CompileSwitch(SwitchStmt stmt)
        {
            string tmp = $"__switch_{_switchCounter++}";
            CompileExpression(stmt.Subject);
            _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(tmp)));

            int endJump = -1;
            var switchBreaks = new List<int>();

            foreach (var caseItem in stmt.Cases)
            {
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(tmp)));
                CompileExpression(caseItem.Value);
                _currentChunk.AddInstruction(new Instruction(OpCode.EQ));
                int nextCase = _currentChunk.Instructions.Count;
                _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));

                // breaks inside the case end the CASE (jump to switch end)
                var savedBreaks = new List<int>(_breakPositions);
                _breakPositions.Clear();
                foreach (var s in caseItem.Body)
                    CompileStatement(s);
                switchBreaks.AddRange(_breakPositions);
                _breakPositions.Clear();
                _breakPositions.AddRange(savedBreaks);

                // no fall-through
                int exitJump = _currentChunk.Instructions.Count;
                _currentChunk.AddInstruction(new Instruction(OpCode.JMP, -1));
                if (endJump < 0) endJump = exitJump;

                _currentChunk.Instructions[nextCase].Operand1 = _currentChunk.Instructions.Count;
            }

            if (stmt.DefaultCase != null)
            {
                var savedBreaks = new List<int>(_breakPositions);
                _breakPositions.Clear();
                if (stmt.DefaultCase is BlockStmt db)
                {
                    foreach (var s in db.Statements)
                        CompileStatement(s);
                }
                else
                {
                    CompileStatement(stmt.DefaultCase);
                }
                switchBreaks.AddRange(_breakPositions);
                _breakPositions.Clear();
                _breakPositions.AddRange(savedBreaks);
            }

            int switchEnd = _currentChunk.Instructions.Count;
            foreach (var pos in switchBreaks)
                _currentChunk.Instructions[pos].Operand1 = switchEnd;
            if (endJump >= 0)
                _currentChunk.Instructions[endJump].Operand1 = switchEnd;
        }

        /// <summary>try/catch/finally. The finally block is duplicated on both
        /// exit paths; `break`/`continue`/`return` inside the try emit TRY_POP
        /// first so handlers never leak.</summary>
        private void CompileTryCatch(TryCatchStmt tc)
        {
            int tryId = _tryIdCounter++;
            int varSlot = string.IsNullOrEmpty(tc.CatchVariable)
                ? 0xFFFFF
                : GetOrAddVariable(tc.CatchVariable);

            int pushPos = _currentChunk.Instructions.Count;
            _currentChunk.AddInstruction(new Instruction(
                OpCode.TRY_PUSH, -1, (_tryIdCounter - 1 << 20) | varSlot));

            _tryIds.Add(tryId);
            if (tc.TryBlock is BlockStmt tb)
            {
                foreach (var s in tb.Statements)
                    CompileStatement(s);
            }
            else if (tc.TryBlock != null)
            {
                CompileStatement(tc.TryBlock);
            }
            _tryIds.RemoveAt(_tryIds.Count - 1);

            _currentChunk.AddInstruction(new Instruction(OpCode.TRY_POP, tryId));

            // finally on the normal path
            if (tc.FinallyBlock is BlockStmt nfb)
            {
                foreach (var s in nfb.Statements)
                    CompileStatement(s);
            }
            else if (tc.FinallyBlock != null)
            {
                CompileStatement(tc.FinallyBlock);
            }

            int endJump = -1;
            if (tc.CatchBlock != null)
            {
                endJump = _currentChunk.Instructions.Count;
                _currentChunk.AddInstruction(new Instruction(OpCode.JMP, -1));
                _currentChunk.Instructions[pushPos].Operand1 = _currentChunk.Instructions.Count;

                if (tc.CatchBlock is BlockStmt cb)
                {
                    foreach (var s in cb.Statements)
                        CompileStatement(s);
                }
                else
                {
                    CompileStatement(tc.CatchBlock);
                }

                // finally on the catch path
                if (tc.FinallyBlock is BlockStmt cfb)
                {
                    foreach (var s in cfb.Statements)
                        CompileStatement(s);
                }
                else if (tc.FinallyBlock != null)
                {
                    CompileStatement(tc.FinallyBlock);
                }
            }
            else
            {
                // no catch: TRY_PUSH keeps CatchIp = -1 → THROW rethrows
                _currentChunk.Instructions[pushPos].Operand1 = -1;
            }

            int endPos = _currentChunk.Instructions.Count;
            if (endJump >= 0)
                _currentChunk.Instructions[endJump].Operand1 = endPos;
        }

        private void EmitTryExits()
        {
            for (int i = _tryIds.Count - 1; i >= 0; i--)
                _currentChunk.AddInstruction(new Instruction(OpCode.TRY_POP, _tryIds[i]));
        }

        private void CompileAsmBlock(AsmBlock block)
        {
            foreach (var inst in block.Instructions)
                CompileAsmInstruction(inst);
        }

        private void CompileAsmInstruction(AsmInstruction inst)
        {
            var lower = inst.Mnemonic.ToLowerInvariant();

            // the VM has no label map — asm control flow can't resolve there
            if (lower == "jmp" || lower == "call" || lower == "ret" ||
                (lower.StartsWith("j") && lower != "jmp") || lower == "loop")
            {
                _errors.Add(
                    $"Bytecode: asm control flow ('{inst.Mnemonic} {inst.Operand1}') is interpreter-only — use `nexus run`");
            }

            var op = lower switch
            {
                "mov" => OpCode.ASM_MOV,
                "push" => OpCode.ASM_PUSH,
                "pop" => OpCode.ASM_POP,
                "add" => OpCode.ASM_ADD,
                "sub" => OpCode.ASM_SUB,
                "mul" => OpCode.ASM_MUL,
                "div" => OpCode.ASM_DIV,
                "inc" => OpCode.ASM_INC,
                "dec" => OpCode.ASM_DEC,
                "cmp" => OpCode.ASM_CMP,
                "jmp" => OpCode.ASM_JMP,
                "je" => OpCode.ASM_JE,
                "jne" => OpCode.ASM_JNE,
                "jg" => OpCode.ASM_JG,
                "jl" => OpCode.ASM_JL,
                "jge" => OpCode.ASM_JGE,
                "jle" => OpCode.ASM_JLE,
                "call" => OpCode.ASM_CALL,
                "ret" => OpCode.ASM_RET,
                "and" => OpCode.ASM_AND,
                "or" => OpCode.ASM_OR,
                "xor" => OpCode.ASM_XOR,
                "not" => OpCode.ASM_NOT,
                "shl" => OpCode.ASM_SHL,
                "shr" => OpCode.ASM_SHR,
                "nop" => OpCode.ASM_NOP,
                "lea" => OpCode.ASM_LEA,
                "test" => OpCode.ASM_TEST,
                _ => OpCode.NOP
            };

            _currentChunk.AddInstruction(new Instruction(op, inst.Operand1, inst.Operand2, inst.Line));
        }

        private void CompileInteropDeclaration(InteropDeclaration decl)
        {
            // bind the interop function VALUE so call sites can dispatch on it
            var interopFn = new NexusInteropFunction(decl.InteropType, decl.Library,
                decl.EntryPoint ?? decl.MethodName);
            _currentChunk.AddInstruction(new Instruction(
                OpCode.LOAD_CONST, _currentChunk.AddConstant(interopFn)));
            _currentChunk.AddInstruction(new Instruction(
                OpCode.STORE_VAR, GetOrAddVariable(decl.MethodName)));

            // keep the legacy marker instruction for the disassembly/compile view
            _currentChunk.AddInstruction(new Instruction(
                OpCode.CALL_INTEROP, decl.InteropType, decl.Library));
        }

        // ============ EXPRESSIONS ============
        private void CompileExpression(Expression expr)
        {
            switch (expr)
            {
                case IntLiteralExpr ile:
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant(ile.Value)));
                    break;

                case FloatLiteralExpr fle:
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant(fle.Value)));
                    break;

                case StringLiteralExpr sle:
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant(sle.Value)));
                    break;

                case BoolLiteralExpr ble:
                    _currentChunk.AddInstruction(new Instruction(
                        ble.Value ? OpCode.LOAD_TRUE : OpCode.LOAD_FALSE));
                    break;

                case NullLiteralExpr:
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    break;

                case ThisExpr:
                    // method/accessor chunks receive `this` via chunk.ThisSlot
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable("this")));
                    break;

                case IdentifierExpr ie when _staticsMap.TryGetValue(ie.Name, out var staticRef):
                {
                    // bare reference to a class/enum static → container.member
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable(staticRef.Item1)));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.GET_MEMBER, _currentChunk.AddConstant(staticRef.Item2)));
                    break;
                }

                case IdentifierExpr ie when _currentInstanceFields != null &&
                                           _currentInstanceFields.Contains(ie.Name) &&
                                           !_declaredLocals.Contains(ie.Name):
                {
                    // C#-style implicit `this` field read inside a member body
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable("this")));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.GET_MEMBER, _currentChunk.AddConstant(ie.Name)));
                    break;
                }

                case IdentifierExpr ie:
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable(ie.Name)));
                    break;

                case BinaryExpr be when be.Operator == "??":
                {
                    // left ?? right — DUP + pop-jump; the non-null left remains
                    // as the expression result
                    CompileExpression(be.Left);
                    _currentChunk.AddInstruction(new Instruction(OpCode.DUP));
                    int nnJump = _currentChunk.Instructions.Count;
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_NOT_NULL, -1));
                    _currentChunk.AddInstruction(new Instruction(OpCode.POP));   // drop null left
                    CompileExpression(be.Right);
                    int endJump = _currentChunk.Instructions.Count;
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP, -1));
                    int after = _currentChunk.Instructions.Count;
                    _currentChunk.Instructions[endJump].Operand1 = after;
                    _currentChunk.Instructions[nnJump].Operand1 = after;
                    break;
                }

                case BinaryExpr be:
                    CompileExpression(be.Left);
                    CompileExpression(be.Right);
                    CompileBinaryOp(be.Operator);
                    break;

                case UnaryExpr ue when ue.IsPostfix && ue.Operand is IdentifierExpr pid:
                {
                    // i++ : store back AND leave the OLD value as the expression result
                    // (keeps the stack balanced for statement form: ExpressionStmt POPs it)
                    int vi = GetOrAddVariable(pid.Name);
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, vi));
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, vi));
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_INT, 1L));
                    _currentChunk.AddInstruction(new Instruction(OpCode.ADD));
                    _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, vi));
                    break;
                }

                case UnaryExpr ue when !ue.IsPostfix &&
                                      (ue.Operator == "++" || ue.Operator == "--") &&
                                      ue.Operand is IdentifierExpr pid2:
                {
                    // ++i : store back AND leave the NEW value
                    int vi = GetOrAddVariable(pid2.Name);
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, vi));
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_INT, 1L));
                    _currentChunk.AddInstruction(new Instruction(
                        ue.Operator == "++" ? OpCode.ADD : OpCode.SUB));
                    _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, vi));
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, vi));
                    break;
                }

                case UnaryExpr ue:
                    CompileExpression(ue.Operand);
                    CompileUnaryOp(ue.Operator);
                    break;

                case AssignExpr ae:
                    CompileAssignment(ae);
                    break;

                case CallExpr ce when ce.Callee is IdentifierExpr id2 && _callNameMap.TryGetValue(id2.Name, out var rewritten):
                {
                    // bare sibling static method call inside a class/enum body
                    int calleeConst2 = _currentChunk.AddConstant(rewritten);
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_CONST, calleeConst2));
                    foreach (var arg in ce.Arguments)
                        CompileExpression(arg);
                    _currentChunk.AddInstruction(new Instruction(OpCode.CALL, ce.Arguments.Count));
                    break;
                }

                case CallExpr ce when ce.Callee is IdentifierExpr id3 && _staticsMap.ContainsKey(id3.Name):
                {
                    // calling a static field that holds a function/lambda
                    var staticRef2 = _staticsMap[id3.Name];
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable(staticRef2.Item1)));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.GET_MEMBER, _currentChunk.AddConstant(staticRef2.Item2)));
                    foreach (var arg in ce.Arguments)
                        CompileExpression(arg);
                    _currentChunk.AddInstruction(new Instruction(OpCode.CALL, ce.Arguments.Count));
                    break;
                }

                case CallExpr ce when ce.Callee is IdentifierExpr id:
                {
                    // load the variable, then call it — for top-level functions the
                    // slot holds the function's name string (bound at decl), for
                    // delegate vars it holds a function value; if the slot is empty
                    // the CALL's fallback name resolves builtins by name
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable(id.Name)));
                    foreach (var arg in ce.Arguments)
                        CompileExpression(arg);
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.CALL, ce.Arguments.Count, id.Name));
                    break;
                }

                case CallExpr ce:
                    CompileExpression(ce.Callee);
                    foreach (var arg in ce.Arguments)
                        CompileExpression(arg);
                    _currentChunk.AddInstruction(new Instruction(OpCode.CALL, ce.Arguments.Count));
                    break;

                case MemberAccessExpr mae when mae.NullConditional:
                {
                    // obj?.member → null when obj is null (JMP_IF_NOT_NULL pops
                    // its operand, so the DUP'd non-null obj feeds GET_MEMBER)
                    CompileExpression(mae.Object);
                    _currentChunk.AddInstruction(new Instruction(OpCode.DUP));
                    int ncJump = _currentChunk.Instructions.Count;
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_NOT_NULL, -1));
                    _currentChunk.AddInstruction(new Instruction(OpCode.POP));   // drop the null
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    int ncEnd = _currentChunk.Instructions.Count;
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP, ncEnd));
                    _currentChunk.Instructions[ncJump].Operand1 = _currentChunk.Instructions.Count;
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.GET_MEMBER, _currentChunk.AddConstant(mae.Member)));
                    _currentChunk.Instructions[ncEnd].Operand1 = _currentChunk.Instructions.Count;
                    break;
                }

                case MemberAccessExpr mae:
                    CompileExpression(mae.Object);
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.GET_MEMBER, _currentChunk.AddConstant(mae.Member)));
                    break;

                case IndexExpr idxExpr:
                    CompileExpression(idxExpr.Object);
                    CompileExpression(idxExpr.Index);
                    _currentChunk.AddInstruction(new Instruction(OpCode.GET_INDEX));
                    break;

                case TernaryExpr te:
                    CompileExpression(te.Condition);
                    int jumpFalse = _currentChunk.Instructions.Count;
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP_IF_FALSE, -1));
                    CompileExpression(te.ThenExpr);
                    int jumpEnd = _currentChunk.Instructions.Count;
                    _currentChunk.AddInstruction(new Instruction(OpCode.JMP, -1));
                    _currentChunk.Instructions[jumpFalse].Operand1 = _currentChunk.Instructions.Count;
                    CompileExpression(te.ElseExpr);
                    _currentChunk.Instructions[jumpEnd].Operand1 = _currentChunk.Instructions.Count;
                    break;

                case RangeExpr re:
                    // a..b → range(a, b) builtin (inclusive on both ends)
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable("range")));
                    CompileExpression(re.Start);
                    CompileExpression(re.End);
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.CALL, 2, "range"));
                    break;

                case ListExpr le:
                    _currentChunk.AddInstruction(new Instruction(OpCode.NEW_LIST));
                    foreach (var elem in le.Elements)
                    {
                        CompileExpression(elem);
                        _currentChunk.AddInstruction(new Instruction(OpCode.LIST_APPEND));
                    }
                    break;

                case AsmModuleExpr:
                    _errors.Add("Bytecode: asm modules (asm in expression position) are interpreter-only for now — use `nexus run`");
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    break;

                case BaseInitCallExpr bse when _currentBaseClassName != null:
                {
                    // super(args) → CALL_SUPER into the base constructor chunk
                    foreach (var arg in bse.Arguments)
                        CompileExpression(arg);
                    int primary = _currentChunk.AddConstant($"{_currentBaseClassName}.init");
                    int fallback = _currentChunk.AddConstant($"{_currentBaseClassName}.constructor");
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.CALL_SUPER, (bse.Arguments.Count << 20) | primary, fallback));
                    break;
                }

                case BaseInitCallExpr:
                    _errors.Add("Bytecode: 'super(...)' outside a class constructor is not supported");
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    break;

                case NewExpr ne:
                {
                    switch (ne.TypeName.ToLowerInvariant())
                    {
                        case "list":
                            _currentChunk.AddInstruction(new Instruction(OpCode.NEW_LIST));
                            break;
                        case "dict":
                            _currentChunk.AddInstruction(new Instruction(OpCode.NEW_DICT));
                            break;
                        case "array":
                            _currentChunk.AddInstruction(new Instruction(OpCode.NEW_ARRAY));
                            break;
                        default:
                            // class value on the stack + args → NEW_INSTANCE
                            _currentChunk.AddInstruction(new Instruction(
                                OpCode.LOAD_VAR, GetOrAddVariable(ne.TypeName)));
                            foreach (var arg in ne.Arguments)
                                CompileExpression(arg);
                            _currentChunk.AddInstruction(new Instruction(
                                OpCode.NEW_INSTANCE, ne.Arguments.Count));
                            break;
                    }
                    break;
                }

                case InterpStringExpr ise:
                {
                    foreach (var part in ise.Parts)
                    {
                        if (part is Expression pe)
                            CompileExpression(pe);
                        else
                            _currentChunk.AddInstruction(new Instruction(
                                OpCode.LOAD_CONST, _currentChunk.AddConstant(part)));
                    }
                    _currentChunk.AddInstruction(new Instruction(OpCode.CONCAT_N, ise.Parts.Count));
                    break;
                }

                case LambdaExpr lam:
                {
                    // lambdas compile to their own chunk; the expression leaves a
                    // callable placeholder. VM closures see globals + params
                    // (no captured locals — interpreter/AOT have full closures).
                    string chunkName = $"__lambda_{_lambdaCounter++}";
                    var funcChunk = new BytecodeChunk { Name = chunkName };
                    foreach (var p in lam.Parameters)
                        funcChunk.ParamSlots.Add(GetOrAddVariable(p.Name));

                    var savedChunk = _currentChunk;
                    var savedInstFields = _currentInstanceFields;
                    _currentInstanceFields = null;   // lambda chunks have no `this` slot
                    _currentChunk = funcChunk;
                    if (lam.IsExpressionBody)
                    {
                        CompileExpression(lam.ExpressionBody);
                        _currentChunk.AddInstruction(new Instruction(OpCode.RETURN));
                    }
                    else
                    {
                        foreach (var s in lam.Body)
                            CompileStatement(s);
                        _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                        _currentChunk.AddInstruction(new Instruction(OpCode.RETURN));
                    }
                    _currentChunk = savedChunk;
                    _currentInstanceFields = savedInstFields;
                    _functions[chunkName] = funcChunk;

                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_CONST, _currentChunk.AddConstant(new NexusFunction(chunkName))));
                    break;
                }

                default:
                    _errors.Add($"Bytecode: unhandled expression '{expr.GetType().Name}'");
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_NULL));
                    break;
            }
        }

        private void CompileAssignment(AssignExpr expr)
        {
            // Assignment is an expression: it MUST leave the assigned value on the
            // stack (ExpressionStmt pops it; chained a = b = 5 works too)
            if (expr.Target is IdentifierExpr idExpr)
            {
                int varIdx;
                if (_currentInstanceFields != null &&
                    _currentInstanceFields.Contains(idExpr.Name) &&
                    !_declaredLocals.Contains(idExpr.Name))
                {
                    // C#-style implicit `this` field/property write
                    int thisIdx = GetOrAddVariable("this");
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, thisIdx));
                    if (expr.Operator != "=")
                    {
                        _currentChunk.AddInstruction(new Instruction(
                            OpCode.GET_MEMBER, _currentChunk.AddConstant(idExpr.Name)));
                        CompileExpression(expr.Value);
                        CompileBinaryOp(expr.Operator.Substring(0, expr.Operator.Length - 1));
                    }
                    else
                    {
                        CompileExpression(expr.Value);
                    }
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.SET_MEMBER, _currentChunk.AddConstant(idExpr.Name)));
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, thisIdx));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.GET_MEMBER, _currentChunk.AddConstant(idExpr.Name)));
                    return;
                }

                if (_staticsMap.TryGetValue(idExpr.Name, out var assignStatic))
                {
                    // static member write inside a class/enum body
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable(assignStatic.Item1)));
                    if (expr.Operator == "=")
                    {
                        CompileExpression(expr.Value);
                    }
                    else
                    {
                        _currentChunk.AddInstruction(new Instruction(
                            OpCode.GET_MEMBER, _currentChunk.AddConstant(assignStatic.Item2)));
                        CompileExpression(expr.Value);
                        CompileBinaryOp(expr.Operator.Substring(0, expr.Operator.Length - 1));
                    }
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.SET_MEMBER, _currentChunk.AddConstant(assignStatic.Item2)));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.LOAD_VAR, GetOrAddVariable(assignStatic.Item1)));
                    _currentChunk.AddInstruction(new Instruction(
                        OpCode.GET_MEMBER, _currentChunk.AddConstant(assignStatic.Item2)));
                    return;
                }

                varIdx = GetOrAddVariable(idExpr.Name);
                if (expr.Operator == "=")
                {
                    CompileExpression(expr.Value);
                }
                else
                {
                    // compound: load current, apply op
                    _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, varIdx));
                    CompileExpression(expr.Value);
                    CompileBinaryOp(expr.Operator.Substring(0, expr.Operator.Length - 1));
                }
                _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, varIdx));
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, varIdx)); // result
                return;
            }

            if (expr.Target is MemberAccessExpr memberExpr)
            {
                // SET_MEMBER pops value then target; a temp slot preserves the
                // assigned value as this expression's result
                string tmp = $"__set_tmp_{_setTmpCounter++}";
                CompileExpression(expr.Value);
                _currentChunk.AddInstruction(new Instruction(OpCode.STORE_VAR, GetOrAddVariable(tmp)));
                CompileExpression(memberExpr.Object);
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(tmp)));
                _currentChunk.AddInstruction(new Instruction(
                    OpCode.SET_MEMBER, _currentChunk.AddConstant(memberExpr.Member)));
                _currentChunk.AddInstruction(new Instruction(OpCode.LOAD_VAR, GetOrAddVariable(tmp)));
                return; // assigned value remains on stack
            }

            // unsupported target: evaluate for side effects, still leave a value
            CompileExpression(expr.Value);
        }

        private void CompileBinaryOp(string op)
        {
            var opCode = op switch
            {
                "+" => OpCode.ADD,
                "-" => OpCode.SUB,
                "*" => OpCode.MUL,
                "/" => OpCode.DIV,
                "%" => OpCode.MOD,
                "==" => OpCode.EQ,
                "!=" => OpCode.NEQ,
                "<" => OpCode.LT,
                ">" => OpCode.GT,
                "<=" => OpCode.LTE,
                ">=" => OpCode.GTE,
                "&&" => OpCode.LOGICAL_AND,
                "||" => OpCode.LOGICAL_OR,
                "&" => OpCode.AND,
                "|" => OpCode.OR,
                "^" => OpCode.XOR,
                "<<" => OpCode.SHL,
                ">>" => OpCode.SHR,
                _ => OpCode.NOP
            };

            _currentChunk.AddInstruction(new Instruction(opCode));
        }

        private void CompileUnaryOp(string op)
        {
            var opCode = op switch
            {
                "-" => OpCode.NEG,
                "!" => OpCode.LOGICAL_NOT,
                "~" => OpCode.NOT,
                "++" => OpCode.INC,
                "--" => OpCode.DEC,
                _ => OpCode.NOP
            };

            _currentChunk.AddInstruction(new Instruction(opCode));
        }

        private int GetOrAddVariable(string name)
        {
            if (!_variableMap.ContainsKey(name))
                _variableMap[name] = _nextVarIndex++;
            return _variableMap[name];
        }
    }
}

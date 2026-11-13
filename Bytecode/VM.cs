using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Core;
using Nexus.Interop;
using Nexus.Runtime;

namespace Nexus.Bytecode
{
    /// <summary>Stack-based virtual machine executing Nexus bytecode.</summary>
    public class VirtualMachine
    {
        private readonly Stack<NexusValue> _stack = new Stack<NexusValue>();
        private List<NexusValue> _locals = new List<NexusValue>();
        private readonly Dictionary<string, NexusValue> _registers = new Dictionary<string, NexusValue>();
        private readonly List<string> _output = new List<string>();
        private readonly InteropManager _interop;
        private long _steps;   // global instruction budget

        // NASM register simulation
        private readonly Dictionary<string, long> _asmRegisters = new Dictionary<string, long>
        {
            ["eax"] = 0, ["ebx"] = 0, ["ecx"] = 0, ["edx"] = 0,
            ["esi"] = 0, ["edi"] = 0, ["ebp"] = 0, ["esp"] = 0,
            ["ax"] = 0, ["bx"] = 0, ["cx"] = 0, ["dx"] = 0,
            ["ah"] = 0, ["al"] = 0, ["bh"] = 0, ["bl"] = 0,
            ["ch"] = 0, ["cl"] = 0, ["dh"] = 0, ["dl"] = 0
        };

        private int _lastComparison;
        private readonly Stack<long> _asmStack = new Stack<long>();
        private readonly List<long> _memory = new List<long>(new long[65536]);

        /// <summary>Classes defined by CLASS_DEFINE — used for instantiation,
        /// property/operator dispatch and constructor lookup.</summary>
        private readonly Dictionary<string, NexusClass> _classes =
            new Dictionary<string, NexusClass>();

        private readonly Stack<TryHandler> _tryHandlers = new Stack<TryHandler>();

        private class TryHandler
        {
            public int CatchIp;
            public int VarSlot;
            public int TryId;
            public int StackDepth;
        }

        /// <summary>Thrown by the THROW opcode; frame wrappers unwind it to the
        /// nearest handler belonging to their own chunk.</summary>
        private class NexusVmThrowException : Exception
        {
            public NexusValue Value { get; }
            public NexusVmThrowException(NexusValue value) : base("throw") { Value = value; }
        }

        public List<string> Output => _output;
        public Dictionary<string, long> Registers => _asmRegisters;

        private const long MaxSteps = 20_000_000;   // runaway-loop guard

        public VirtualMachine(InteropManager interop = null)
        {
            _interop = interop ?? new InteropManager();
        }

        // ============ builtins callable from bytecode ============
        private static readonly Random _rng = new Random();

        private static readonly Dictionary<string, Func<List<NexusValue>, NexusValue>> Builtins =
            new Dictionary<string, Func<List<NexusValue>, NexusValue>>
            {
                ["println"] = a => { Console.WriteLine(string.Join(" ", a.Select(x => x?.ToString() ?? ""))); return NexusNull.Instance; },
                ["print"]   = a => { Console.Write(string.Join(" ", a.Select(x => x?.ToString() ?? ""))); return NexusNull.Instance; },
                ["str"]     = a => new NexusString(a.Count > 0 ? a[0]?.ToString() ?? "" : ""),
                ["len"]     = a => a.Count == 0 ? new NexusInt(0) : a[0] switch
                {
                    NexusString s  => new NexusInt(s.Value.Length),
                    NexusList l    => new NexusInt(l.Items.Count),
                    NexusDict d    => new NexusInt(d.Entries.Count),
                    NexusArray arr => new NexusInt(arr.Elements.Length),
                    _ => new NexusInt(0)
                },
                ["int"]     = a => new NexusInt(a.Count > 0 ? ValueHelpers.ToInteger(a[0]) : 0),
                ["float"]   = a => new NexusFloat(a.Count > 0 ? ValueHelpers.ToNumber(a[0]) : 0),
                ["abs"]     = a => new NexusFloat(Math.Abs(ValueHelpers.ToNumber(a[0]))),
                ["sqrt"]    = a => new NexusFloat(Math.Sqrt(ValueHelpers.ToNumber(a[0]))),
                ["pow"]     = a => new NexusFloat(Math.Pow(ValueHelpers.ToNumber(a[0]), ValueHelpers.ToNumber(a[1]))),
                ["floor"]   = a => new NexusInt((long)Math.Floor(ValueHelpers.ToNumber(a[0]))),
                ["ceil"]    = a => new NexusInt((long)Math.Ceiling(ValueHelpers.ToNumber(a[0]))),
                ["range"]   = a => a.Count < 2
                                ? new NexusList()
                                : Interpreter.BuildRange(a[0], a[1]),
                ["typeof"]  = a => new NexusString(a.Count > 0 ? a[0].TypeName : "null"),
                ["input"]   = _ => new NexusString(Console.ReadLine() ?? ""),
                ["time"]    = _ => new NexusInt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                ["push"]    = a =>
                {
                    if (a.Count >= 2 && a[0] is NexusList l) l.Items.Add(a[1]);
                    return a.Count > 0 ? a[0] : NexusNull.Instance;
                },
                ["pop"]     = a =>
                {
                    if (a.Count >= 1 && a[0] is NexusList l && l.Items.Count > 0)
                    {
                        var v = l.Items[l.Items.Count - 1];
                        l.Items.RemoveAt(l.Items.Count - 1);
                        return v;
                    }
                    return NexusNull.Instance;
                },
                ["random"]  = a => a.Count == 0 ? new NexusFloat(_rng.NextDouble())
                              : a.Count == 1 ? new NexusInt(_rng.Next((int)ValueHelpers.ToInteger(a[0])))
                              : new NexusInt(_rng.Next((int)ValueHelpers.ToInteger(a[0]), (int)ValueHelpers.ToInteger(a[1]))),

                // printf: translate C-style specifiers like the interpreter does
                ["printf"] = a =>
                {
                    if (a.Count == 0) return NexusNull.Instance;
                    var format = a[0].ToString() ?? "";
                    var sb = new System.Text.StringBuilder();
                    int argIndex = 0;
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
                        a.Skip(1).Select(ValueHelpers.ToObject).ToArray());
                    Console.Write(formatted);
                    return NexusNull.Instance;
                },
            };

        public NexusValue Execute(Dictionary<string, BytecodeChunk> chunks, string entryPoint = "__main__")
        {
            if (!chunks.TryGetValue(entryPoint, out var chunk))
                throw new NexusRuntimeException($"Entry point '{entryPoint}' not found in bytecode.");

            // run through the frame wrapper so try/catch inside __main__ works
            return InvokeChunk(chunk, chunks, new List<NexusValue>());
        }

        private NexusValue ExecuteChunk(BytecodeChunk chunk, Dictionary<string, BytecodeChunk> allChunks,
                                        int startIp = 0)
        {
            int ip = startIp;

            while (ip < chunk.Instructions.Count)
            {
                if (++_steps > MaxSteps)
                    throw new NexusRuntimeException(
                        $"VM: exceeded {MaxSteps} instructions — possible infinite loop.");

                var instr = chunk.Instructions[ip];

                switch (instr.Op)
                {
                    // ============ STACK ============
                    case OpCode.NOP: break;
                    case OpCode.POP: if (_stack.Count > 0) _stack.Pop(); break;
                    case OpCode.DUP: if (_stack.Count > 0) _stack.Push(_stack.Peek()); break;
                    case OpCode.SWAP:
                        if (_stack.Count >= 2)
                        {
                            var sa = _stack.Pop();
                            var sb2 = _stack.Pop();
                            _stack.Push(sa);
                            _stack.Push(sb2);
                        }
                        break;

                    // ============ CONSTANTS ============
                    case OpCode.LOAD_CONST:
                        if (instr.Operand1 is int constIdx && constIdx < chunk.ConstantPool.Count)
                            _stack.Push(ValueHelpers.FromObject(chunk.ConstantPool[constIdx]));
                        break;
                    case OpCode.LOAD_NULL: _stack.Push(NexusNull.Instance); break;
                    case OpCode.LOAD_TRUE: _stack.Push(new NexusBool(true)); break;
                    case OpCode.LOAD_FALSE: _stack.Push(new NexusBool(false)); break;
                    case OpCode.LOAD_INT:
                        if (instr.Operand1 is long intVal)
                            _stack.Push(new NexusInt(intVal));
                        break;

                    // ============ VARIABLES ============
                    case OpCode.LOAD_VAR:
                    case OpCode.LOAD_GLOBAL:
                    case OpCode.LOAD_LOCAL:
                        if (instr.Operand1 is int varIdx)
                        {
                            while (_locals.Count <= varIdx)
                                _locals.Add(NexusNull.Instance);
                            _stack.Push(_locals[varIdx]);
                        }
                        break;

                    case OpCode.STORE_VAR:
                    case OpCode.STORE_GLOBAL:
                    case OpCode.STORE_LOCAL:
                        if (instr.Operand1 is int storeIdx && _stack.Count > 0)
                        {
                            var value = _stack.Pop();
                            while (_locals.Count <= storeIdx)
                                _locals.Add(NexusNull.Instance);
                            _locals[storeIdx] = value;
                        }
                        break;

                    // ============ ARITHMETIC (value-aware + overloads) ============
                    case OpCode.ADD:
                    {
                        Pop2(out var adA, out var adB);
                        _stack.Push(TryEventOp(adA, adB, "+")
                                    ?? TryUserOp(adA, adB, "+", 2, allChunks)
                                    ?? AddVals(adA, adB));
                        break;
                    }
                    case OpCode.SUB:
                    {
                        Pop2(out var suA, out var suB);
                        _stack.Push(TryEventOp(suA, suB, "-")
                                    ?? TryUserOp(suA, suB, "-", 2, allChunks)
                                    ?? SubVals(suA, suB));
                        break;
                    }
                    case OpCode.MUL:
                    {
                        Pop2(out var muA, out var muB);
                        _stack.Push(TryUserOp(muA, muB, "*", 2, allChunks) ?? MulVals(muA, muB));
                        break;
                    }
                    case OpCode.DIV:
                    {
                        Pop2(out var diA, out var diB);
                        _stack.Push(TryUserOp(diA, diB, "/", 2, allChunks) ?? DivVals(diA, diB));
                        break;
                    }
                    case OpCode.MOD:
                    {
                        Pop2(out var moA, out var moB);
                        _stack.Push(TryUserOp(moA, moB, "%", 2, allChunks) ?? ModVals(moA, moB));
                        break;
                    }
                    case OpCode.NEG:
                    {
                        if (_stack.Count > 0)
                        {
                            var negOperand = _stack.Pop();
                            var negRes = TryUserOp(negOperand, null, "-", 1, allChunks);
                            _stack.Push(negRes ?? new NexusInt(-ValueHelpers.ToInteger(negOperand)));
                        }
                        break;
                    }
                    case OpCode.INC: if (_stack.Count > 0) _stack.Push(new NexusInt(PopLong() + 1)); break;
                    case OpCode.DEC: if (_stack.Count > 0) _stack.Push(new NexusInt(PopLong() - 1)); break;

                    // ============ BITWISE ============
                    case OpCode.AND: BinaryLong((a, b) => a & b); break;
                    case OpCode.OR: BinaryLong((a, b) => a | b); break;
                    case OpCode.XOR: BinaryLong((a, b) => a ^ b); break;
                    case OpCode.NOT: if (_stack.Count > 0) _stack.Push(new NexusInt(~PopLong())); break;
                    case OpCode.SHL: BinaryLong((a, b) => a << (int)b); break;
                    case OpCode.SHR: BinaryLong((a, b) => a >> (int)b); break;

                    // ============ COMPARISON (with overloads) ============
                    case OpCode.EQ:
                    {
                        Pop2(out var eqA, out var eqB);
                        var eqRes = TryUserOp(eqA, eqB, "==", 2, allChunks);
                        _stack.Push(eqRes ?? new NexusBool(ValsEqual(eqA, eqB)));
                        break;
                    }
                    case OpCode.NEQ:
                    {
                        Pop2(out var neA, out var neB);
                        var neRes = TryUserOp(neA, neB, "!=", 2, allChunks);
                        _stack.Push(neRes ?? new NexusBool(!ValsEqual(neA, neB)));
                        break;
                    }
                    case OpCode.LT:
                    {
                        Pop2(out var ltA, out var ltB);
                        var ltRes = TryUserOp(ltA, ltB, "<", 2, allChunks);
                        _stack.Push(ltRes ?? new NexusBool(CompareVals(ltA, ltB) < 0));
                        break;
                    }
                    case OpCode.GT:
                    {
                        Pop2(out var gtA, out var gtB);
                        var gtRes = TryUserOp(gtA, gtB, ">", 2, allChunks);
                        _stack.Push(gtRes ?? new NexusBool(CompareVals(gtA, gtB) > 0));
                        break;
                    }
                    case OpCode.LTE:
                    {
                        Pop2(out var leA, out var leB);
                        var leRes = TryUserOp(leA, leB, "<=", 2, allChunks);
                        _stack.Push(leRes ?? new NexusBool(CompareVals(leA, leB) <= 0));
                        break;
                    }
                    case OpCode.GTE:
                    {
                        Pop2(out var geA, out var geB);
                        var geRes = TryUserOp(geA, geB, ">=", 2, allChunks);
                        _stack.Push(geRes ?? new NexusBool(CompareVals(geA, geB) >= 0));
                        break;
                    }

                    // ============ LOGICAL ============
                    case OpCode.LOGICAL_AND:
                        if (_stack.Count >= 2)
                        {
                            var ra = _stack.Pop();
                            var la = _stack.Pop();
                            _stack.Push(new NexusBool(la.IsTruthy() && ra.IsTruthy()));
                        }
                        break;

                    case OpCode.LOGICAL_OR:
                        if (_stack.Count >= 2)
                        {
                            var rOr = _stack.Pop();
                            var lOr = _stack.Pop();
                            _stack.Push(new NexusBool(lOr.IsTruthy() || rOr.IsTruthy()));
                        }
                        break;

                    case OpCode.LOGICAL_NOT:
                        if (_stack.Count > 0)
                        {
                            var vNot = _stack.Pop();
                            var notRes = TryUserOp(vNot, null, "!", 1, allChunks);
                            _stack.Push(notRes ?? new NexusBool(!vNot.IsTruthy()));
                        }
                        break;

                    // ============ CONTROL FLOW ============
                    case OpCode.JMP:
                        if (instr.Operand1 is int jmpTarget)
                            ip = jmpTarget - 1;
                        break;

                    case OpCode.JMP_IF_FALSE:
                        if (_stack.Count > 0)
                        {
                            var condF = _stack.Pop();
                            if (!condF.IsTruthy() && instr.Operand1 is int falseTarget)
                                ip = falseTarget - 1;
                        }
                        break;

                    case OpCode.JMP_IF_TRUE:
                        if (_stack.Count > 0)
                        {
                            var condT = _stack.Pop();
                            if (condT.IsTruthy() && instr.Operand1 is int trueTarget)
                                ip = trueTarget - 1;
                        }
                        break;

                    case OpCode.JMP_IF_NOT_NULL:
                        // POP semantics (mirrors JMP_IF_FALSE): the tested value
                        // is consumed; compilers DUP when they still need it
                        if (_stack.Count > 0)
                        {
                            var condNn = _stack.Pop();
                            if (!(condNn is NexusNull) && instr.Operand1 is int nnTarget)
                                ip = nnTarget - 1;
                        }
                        break;

                    // ============ FUNCTIONS ============
                    case OpCode.CALL:
                    {
                        if (!(instr.Operand1 is int argCount))
                            throw new NexusRuntimeException(
                                $"VM: CALL missing argument count (offset {instr.Offset})");

                        var callArgs = new List<NexusValue>();
                        for (int i = 0; i < argCount && _stack.Count > 0; i++)
                            callArgs.Insert(0, _stack.Pop());

                        if (_stack.Count == 0)
                            throw new NexusRuntimeException(
                                $"VM: CALL with no callee on the stack (offset {instr.Offset})");

                        var callee = _stack.Pop();

                        // empty slot + fallback name → resolve as builtin/chunk name
                        if (callee is NexusNull && instr.Operand2 is string fallback)
                            callee = new NexusString(fallback);

                        _stack.Push(CallValue(callee, callArgs, allChunks));
                        break;
                    }

                    case OpCode.CALL_NATIVE:
                        break;

                    case OpCode.CALL_INTEROP:
                        if (instr.Operand1 is string interopType && instr.Operand2 is string library)
                        {
                            var interopResult = _interop.CallInterop(
                                new NexusInteropFunction(interopType, library, library),
                                new List<NexusValue>());
                            _stack.Push(interopResult);
                        }
                        break;

                    case OpCode.RETURN:
                        return _stack.Count > 0 ? _stack.Pop() : NexusNull.Instance;

                    case OpCode.CALL_SUPER:
                    {
                        // operand1: (argCount << 20) | constIdx("Base.init")
                        // operand2: constIdx("Base.constructor") fallback
                        int packed = instr.Operand1 is int sp ? sp : 0;
                        int superArgCount = packed >> 20;
                        var superArgs = new List<NexusValue>();
                        for (int i = 0; i < superArgCount && _stack.Count > 0; i++)
                            superArgs.Insert(0, _stack.Pop());

                        NexusValue superThis = chunk.ThisSlot is int sts && sts < _locals.Count
                            ? _locals[sts] : null;

                        string superName = null;
                        if ((packed & 0xFFFFF) < chunk.ConstantPool.Count)
                            superName = chunk.ConstantPool[packed & 0xFFFFF] as string;

                        string fallbackName = null;
                        if (instr.Operand2 is int fbIdx && fbIdx < chunk.ConstantPool.Count)
                            fallbackName = chunk.ConstantPool[fbIdx] as string;

                        if (superName != null && allChunks.ContainsKey(superName))
                            InvokeChunkByName(superName, allChunks, superArgs, superThis);
                        else if (fallbackName != null && allChunks.ContainsKey(fallbackName))
                            InvokeChunkByName(fallbackName, allChunks, superArgs, superThis);
                        else
                            throw new NexusRuntimeException($"VM: base constructor '{superName}' not found");
                        break;
                    }

                    case OpCode.TRY_PUSH:
                    {
                        int packed = instr.Operand2 is int p2 ? p2 : 0;
                        int varSlot = (packed & 0xFFFFF) == 0xFFFFF ? -1 : (packed & 0xFFFFF);
                        _tryHandlers.Push(new TryHandler
                        {
                            CatchIp = instr.Operand1 is int ci ? ci : -1,
                            VarSlot = varSlot,
                            TryId = packed >> 20,
                            StackDepth = _stack.Count
                        });
                        break;
                    }

                    case OpCode.TRY_POP:
                    {
                        int popId = instr.Operand1 is int pi ? pi : -1;
                        if (_tryHandlers.Count > 0 && _tryHandlers.Peek().TryId == popId)
                            _tryHandlers.Pop();
                        break;
                    }

                    // ============ DATA STRUCTURES ============
                    case OpCode.NEW_LIST: _stack.Push(new NexusList()); break;
                    case OpCode.NEW_DICT: _stack.Push(new NexusDict()); break;
                    case OpCode.NEW_ARRAY: _stack.Push(new NexusArray(new NexusValue[0])); break;
                    case OpCode.DICT_KEYS:
                        // top of stack: dict → key list; other iterables pass through
                        if (_stack.Count > 0 && _stack.Peek() is NexusDict dk)
                        {
                            _stack.Pop();
                            _stack.Push(new NexusList(
                                dk.Entries.Keys.Select(k => (NexusValue)new NexusString(k)).ToList()));
                        }
                        break;

                    case OpCode.CONCAT_N:
                    {
                        int cn = instr.Operand1 is int cN ? cN : 0;
                        cn = Math.Min(cn, _stack.Count);
                        var parts = new List<string>(cn);
                        for (int i = 0; i < cn; i++)
                            parts.Insert(0, _stack.Pop()?.ToString() ?? "");
                        _stack.Push(new NexusString(string.Concat(parts)));
                        break;
                    }

                    case OpCode.LIST_APPEND:
                        if (_stack.Count >= 2)
                        {
                            var item = _stack.Pop();
                            if (_stack.Peek() is NexusList l) l.Items.Add(item);
                        }
                        break;

                    // ============ MEMBER ACCESS ============
                    case OpCode.GET_MEMBER:
                    {
                        var memberName = ResolveStringOperand(instr.Operand1, chunk);
                        if (memberName != null && _stack.Count > 0)
                        {
                            var obj = _stack.Pop();
                            _stack.Push(GetMemberVal(obj, memberName, allChunks));
                        }
                        break;
                    }

                    case OpCode.SET_MEMBER:
                    {
                        var setMember = ResolveStringOperand(instr.Operand1, chunk);
                        if (setMember != null && _stack.Count >= 2)
                        {
                            var setValue = _stack.Pop();
                            var targetObj = _stack.Pop();
                            SetMemberVal(targetObj, setMember, setValue, allChunks);
                        }
                        break;
                    }

                    case OpCode.GET_INDEX:
                        if (_stack.Count >= 2)
                        {
                            var idx = _stack.Pop();
                            var container = _stack.Pop();
                            _stack.Push(GetIndexed(container, idx));
                        }
                        break;

                    // ============ NASM/ASM ============
                    case OpCode.ASM_MOV: HandleAsmMov(instr); break;
                    case OpCode.ASM_PUSH:
                        if (_stack.Count > 0) _asmStack.Push(PopLong());
                        break;
                    case OpCode.ASM_POP:
                        _stack.Push(new NexusInt(_asmStack.Count > 0 ? _asmStack.Pop() : 0));
                        break;
                    case OpCode.ASM_ADD: HandleAsmBinaryOp(instr, (a, b) => a + b); break;
                    case OpCode.ASM_SUB: HandleAsmBinaryOp(instr, (a, b) => a - b); break;
                    case OpCode.ASM_MUL: HandleAsmBinaryOp(instr, (a, b) => a * b); break;
                    case OpCode.ASM_DIV: HandleAsmBinaryOp(instr, (a, b) => b != 0 ? a / b : 0); break;
                    case OpCode.ASM_INC: HandleAsmUnaryOp(instr, v => v + 1); break;
                    case OpCode.ASM_DEC: HandleAsmUnaryOp(instr, v => v - 1); break;
                    case OpCode.ASM_CMP: HandleAsmCompare(instr); break;
                    case OpCode.ASM_JE: if (_lastComparison == 0 && instr.Operand1 is int jeT) ip = jeT - 1; break;
                    case OpCode.ASM_JNE: if (_lastComparison != 0 && instr.Operand1 is int jneT) ip = jneT - 1; break;
                    case OpCode.ASM_JG: if (_lastComparison > 0 && instr.Operand1 is int jgT) ip = jgT - 1; break;
                    case OpCode.ASM_JL: if (_lastComparison < 0 && instr.Operand1 is int jlT) ip = jlT - 1; break;
                    case OpCode.ASM_JGE: if (_lastComparison >= 0 && instr.Operand1 is int jgeT) ip = jgeT - 1; break;
                    case OpCode.ASM_JLE: if (_lastComparison <= 0 && instr.Operand1 is int jleT) ip = jleT - 1; break;
                    case OpCode.ASM_AND: HandleAsmBinaryOp(instr, (a, b) => a & b); break;
                    case OpCode.ASM_OR: HandleAsmBinaryOp(instr, (a, b) => a | b); break;
                    case OpCode.ASM_XOR: HandleAsmBinaryOp(instr, (a, b) => a ^ b); break;
                    case OpCode.ASM_NOT: HandleAsmUnaryOp(instr, v => ~v); break;
                    case OpCode.ASM_SHL: HandleAsmBinaryOp(instr, (a, b) => a << (int)b); break;
                    case OpCode.ASM_SHR: HandleAsmBinaryOp(instr, (a, b) => a >> (int)b); break;
                    case OpCode.ASM_NOP: break;
                    case OpCode.ASM_LABEL: break;

                    // ============ OUTPUT (count-aware) ============
                    case OpCode.PRINT:
                    case OpCode.PRINTLN:
                    {
                        int count = instr.Operand1 is int c ? c : 1;
                        count = Math.Min(count, _stack.Count);

                        // values were pushed left-to-right, so the last arg is on
                        // top — pop then insert at front to restore argument order
                        var parts = new List<string>(count);
                        for (int i = 0; i < count; i++)
                            parts.Insert(0, _stack.Pop().ToString() ?? "");

                        var text = string.Join(" ", parts);
                        if (instr.Op == OpCode.PRINTLN)
                        {
                            Console.WriteLine(text);
                            _output.Add(text);
                        }
                        else
                        {
                            Console.Write(text);
                            _output.Add(text);
                        }
                        break;
                    }

                    case OpCode.THROW:
                    {
                        var throwVal = _stack.Count > 0 ? _stack.Pop() : NexusNull.Instance;
                        // unwinding is handled by the frame wrappers (InvokeChunk /
                        // Execute) which resume at the catch IP of their own chunk
                        throw new NexusVmThrowException(throwVal);
                    }

                    case OpCode.HALT:
                        return _stack.Count > 0 ? _stack.Peek() : NexusNull.Instance;

                    // ============ CLASSES ============
                    case OpCode.CLASS_DEFINE:
                    {
                        if (instr.Operand1 is int metaIdx &&
                            metaIdx < chunk.ConstantPool.Count &&
                            chunk.ConstantPool[metaIdx] is NexusClass metaCls)
                        {
                            _classes[metaCls.Name] = metaCls;
                            _stack.Push(metaCls);
                        }
                        else if (instr.Operand1 is string clsName)
                        {
                            var hollow = new NexusClass(clsName);
                            _classes[clsName] = hollow;
                            _stack.Push(hollow);
                        }
                        break;
                    }

                    case OpCode.NEW_INSTANCE:
                    {
                        int niArgs = instr.Operand1 is int niCount ? niCount : 0;
                        var niCallArgs = new List<NexusValue>();
                        for (int i = 0; i < niArgs && _stack.Count > 0; i++)
                            niCallArgs.Insert(0, _stack.Pop());

                        if (_stack.Count == 0)
                            throw new NexusRuntimeException(
                                $"VM: NEW_INSTANCE with no class on the stack (offset {instr.Offset})");

                        var typeVal = _stack.Pop();
                        NexusClass targetCls = typeVal as NexusClass;
                        if (targetCls == null && typeVal is NexusString nameStr)
                            _classes.TryGetValue(nameStr.Value, out targetCls);

                        if (targetCls == null)
                            throw new NexusRuntimeException(
                                $"VM: cannot instantiate unknown type '{typeVal}'");

                        _stack.Push(InstantiateVM(targetCls, niCallArgs, allChunks));
                        break;
                    }

                    default:
                        break;
                }

                ip++;
            }

            return _stack.Count > 0 ? _stack.Peek() : NexusNull.Instance;
        }

        // ============ function invocation ============
        private NexusValue InvokeChunk(BytecodeChunk chunk, Dictionary<string, BytecodeChunk> allChunks,
                                       List<NexusValue> args, NexusValue thisValue = null)
        {
            var savedLocals = _locals;

            // copy the caller's frame so globals (and the caller's locals) stay
            // visible, while writes stay frame-local (recursion-safe)
            _locals = new List<NexusValue>(savedLocals);

            if (chunk.ParamSlots.Count > 0)
            {
                // bind arguments by recorded slot (robust against out-of-order
                // slot allocation across the whole program)
                for (int i = 0; i < chunk.ParamSlots.Count && i < args.Count; i++)
                {
                    int slot = chunk.ParamSlots[i];
                    while (_locals.Count <= slot) _locals.Add(NexusNull.Instance);
                    _locals[slot] = args[i];
                }
            }
            else
            {
                for (int i = 0; i < args.Count; i++)
                {
                    while (_locals.Count <= i) _locals.Add(NexusNull.Instance);
                    _locals[i] = args[i];
                }
            }

            if (thisValue != null && chunk.ThisSlot is int thisSlot)
            {
                while (_locals.Count <= thisSlot) _locals.Add(NexusNull.Instance);
                _locals[thisSlot] = thisValue;
            }

            // exception unwinding: a THROW lands on the innermost handler that
            // belongs to THIS chunk; handlers from caller frames propagate up.
            // Runtime errors (division by zero, etc.) are catchable too.
            int handlerFloor = _tryHandlers.Count;
            int ip = 0;
            try
            {
                while (true)
                {
                    try
                    {
                        return ExecuteChunk(chunk, allChunks, ip);
                    }
                    catch (NexusVmThrowException thr)
                    {
                        if (_tryHandlers.Count <= handlerFloor)
                            throw;   // no handler in this frame — propagate

                        var h = _tryHandlers.Pop();
                        while (_stack.Count > h.StackDepth)
                            _stack.Pop();

                        if (h.VarSlot >= 0)
                        {
                            while (_locals.Count <= h.VarSlot) _locals.Add(NexusNull.Instance);
                            _locals[h.VarSlot] = thr.Value;
                        }

                        if (h.CatchIp < 0)
                            throw;   // try without catch — rethrow

                        ip = h.CatchIp;   // resume INSIDE this chunk at the catch block
                    }
                    catch (NexusRuntimeException rex)
                    {
                        if (_tryHandlers.Count <= handlerFloor)
                            throw;   // no handler in this frame — propagate

                        var h = _tryHandlers.Pop();
                        while (_stack.Count > h.StackDepth)
                            _stack.Pop();

                        if (h.VarSlot >= 0)
                        {
                            while (_locals.Count <= h.VarSlot) _locals.Add(NexusNull.Instance);
                            _locals[h.VarSlot] = new NexusString(rex.Message);
                        }

                        if (h.CatchIp < 0)
                            throw;

                        ip = h.CatchIp;
                    }
                }
            }
            finally
            {
                _locals = savedLocals;   // fresh frame per call => recursion works
            }
        }

        private NexusValue InvokeChunkByName(string name, Dictionary<string, BytecodeChunk> chunks,
                                             List<NexusValue> args, NexusValue thisValue = null)
        {
            if (!chunks.TryGetValue(name, out var chunk))
                throw new NexusRuntimeException($"VM: function '{name}' has no compiled bytecode");
            return InvokeChunk(chunk, chunks, args, thisValue);
        }

        /// <summary>
        /// Universal call dispatch: named builtins, compiled chunks,
        /// functions, classes (construction) and events (multicast invoke).
        /// </summary>
        private NexusValue CallValue(NexusValue callee, List<NexusValue> args,
                                     Dictionary<string, BytecodeChunk> allChunks)
        {
            switch (callee)
            {
                case NexusString nameVal when !string.IsNullOrEmpty(nameVal.Value):
                {
                    var name = nameVal.Value;
                    if (Builtins.TryGetValue(name, out var builtin))
                        return builtin(args);
                    if (allChunks.TryGetValue(name, out var namedChunk))
                        return InvokeChunk(namedChunk, allChunks, args);
                    throw new NexusRuntimeException($"VM: unknown function '{name}'");
                }

                case NexusInteropFunction interopFn:
                    return _interop.CallInterop(interopFn, args);

                case NexusFunction nf:
                    if (nf.IsNative && nf.NativeImplementation != null)
                        return nf.NativeImplementation(args);
                    if (allChunks.TryGetValue(nf.Name, out var chunk))
                        return InvokeChunk(chunk, allChunks, args, nf.BoundThis);
                    throw new NexusRuntimeException($"VM: function '{nf.Name}' has no compiled bytecode");

                case NexusClass cls:
                    return InstantiateVM(cls, args, allChunks);

                case NexusEvent ev:
                {
                    NexusValue last = NexusNull.Instance;
                    foreach (var handler in ev.Handlers.ToList())
                        last = CallValue(handler, args, allChunks);
                    return last;
                }

                default:
                    throw new NexusRuntimeException(
                        $"VM: cannot call value of type '{callee.TypeName}'");
            }
        }

        /// <summary>Construct a VM instance: run field initializers, create per-
        /// instance events, then run the constructor if one exists.</summary>
        private NexusValue InstantiateVM(NexusClass cls, List<NexusValue> args,
                                         Dictionary<string, BytecodeChunk> chunks)
        {
            var instance = new NexusInstance(cls);

            if (chunks.TryGetValue($"__fields_{cls.Name}", out var fieldsChunk))
            {
                if (InvokeChunk(fieldsChunk, chunks, new List<NexusValue>()) is NexusList values)
                {
                    for (int i = 0; i < cls.FieldDeclarations.Count && i < values.Items.Count; i++)
                        if (cls.FieldDeclarations[i] is VariableDecl vd)
                            instance.Fields[vd.Name] = values.Items[i];
                }
            }

            foreach (var evName in cls.EventNames)
                instance.Fields[evName] = new NexusEvent(evName);

            if (cls.FindMethod("init") is NexusFunction ctor)
                InvokeChunkByName(ctor.Name, chunks, args, instance);
            else if (cls.FindMethod("constructor") is NexusFunction ctor2)
                InvokeChunkByName(ctor2.Name, chunks, args, instance);

            return instance;
        }

        // ============ member access with property routing ============
        private NexusValue GetMemberVal(NexusValue obj, string name,
                                        Dictionary<string, BytecodeChunk> chunks)
        {
            switch (obj)
            {
                case NexusInstance inst:
                {
                    var prop = inst.Class?.FindProperty(name);
                    if (prop != null)
                    {
                        if (prop.GetterIsAuto)
                            return inst.Fields.TryGetValue(name, out var backing)
                                ? backing : NexusNull.Instance;
                        if (prop.Getter != null)
                            return InvokeChunkByName(prop.Getter.Name, chunks,
                                new List<NexusValue>(), inst);
                        throw new NexusRuntimeException($"Property '{name}' has no getter");
                    }
                    return inst.GetField(name);
                }

                case NexusClass cls:
                    if (cls.StaticFields.TryGetValue(name, out var sv)) return sv;
                    return cls.FindMethod(name);

                case NexusDict dict:
                    return dict.Entries.TryGetValue(name, out var mv) ? mv : NexusNull.Instance;

                case NexusEnum e:
                    if (name == "name") return new NexusString(e.MemberName);
                    if (name == "value") return new NexusInt(e.Value);
                    return NexusNull.Instance;

                case NexusEvent ev:
                    if (name == "count" || name == "handlerCount")
                        return new NexusInt(ev.Handlers.Count);
                    return NexusNull.Instance;

                case NexusString str:
                    switch (name)
                    {
                        case "length": case "len": return new NexusInt(str.Value.Length);
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

                case NexusList list:
                    switch (name)
                    {
                        case "length": case "len": case "count":
                            return new NexusInt(list.Items.Count);
                        case "first": return list.Items.Count > 0 ? list.Items[0] : NexusNull.Instance;
                        case "last": return list.Items.Count > 0 ? list.Items[list.Items.Count - 1] : NexusNull.Instance;
                        case "empty": return new NexusBool(list.Items.Count == 0);
                        default: return NexusNull.Instance;
                    }

                default:
                    return NexusNull.Instance;
            }
        }

        private void SetMemberVal(NexusValue target, string name, NexusValue value,
                                  Dictionary<string, BytecodeChunk> chunks)
        {
            switch (target)
            {
                case NexusInstance ti:
                {
                    var prop = ti.Class?.FindProperty(name);
                    if (prop != null)
                    {
                        if (prop.Setter == null)
                            throw new NexusRuntimeException($"Property '{name}' has no setter");
                        if (!prop.SetterIsAuto)
                        {
                            InvokeChunkByName(prop.Setter.Name, chunks,
                                new List<NexusValue> { value }, ti);
                            return;
                        }
                    }
                    ti.SetField(name, value);
                    break;
                }
                case NexusClass tc:
                    tc.StaticFields[name] = value;
                    break;
                case NexusDict td:
                    td.Entries[name] = value;
                    break;
                default:
                    break;
            }
        }

        // ============ operator overload / event dispatch ============
        private NexusValue TryUserOp(NexusValue l, NexusValue r, string symbol, int arity,
                                     Dictionary<string, BytecodeChunk> chunks)
        {
            NexusClass cls;
            NexusValue thisVal, other;
            if (l is NexusInstance li) { cls = li.Class; thisVal = l; other = r; }
            else if (r is NexusInstance ri) { cls = ri.Class; thisVal = r; other = l; }
            else return null;

            int declArity = arity == 2 ? 1 : 0;

            // method-style: this = left operand, single arg = other operand
            var fn = cls.FindOperator(symbol, declArity);
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
                return CallValue(bound, arity == 2
                    ? new List<NexusValue> { other }
                    : new List<NexusValue>(), chunks);
            }

            // C#-style static form: operator +(a, b)
            if (arity == 2)
            {
                var staticFn = cls.FindOperator(symbol, 2);
                if (staticFn != null)
                    return CallValue(staticFn, new List<NexusValue> { l, r }, chunks);
            }

            return null;
        }

        private static NexusValue TryEventOp(NexusValue l, NexusValue r, string op)
        {
            // handler may be a function value or (VM path) a function NAME string
            if (l is NexusEvent ev &&
                (r is NexusFunction || r is NexusInteropFunction || r is NexusString))
            {
                if (op == "+") ev.AddHandler(r);
                else if (op == "-") ev.RemoveHandler(r);
                else return null;
                return ev;
            }
            return null;
        }

        // ============ value-aware arithmetic ============
        private static NexusValue AddVals(NexusValue l, NexusValue r)
        {
            if (l is NexusString || r is NexusString)
                return new NexusString(l.ToString() + r.ToString());
            if (l is NexusList ll && r is NexusList rl)
            {
                var combined = new List<NexusValue>(ll.Items);
                combined.AddRange(rl.Items);
                return new NexusList(combined);
            }
            if (l is NexusFloat || r is NexusFloat)
                return new NexusFloat(ValueHelpers.ToNumber(l) + ValueHelpers.ToNumber(r));
            return new NexusInt(ValueHelpers.ToInteger(l) + ValueHelpers.ToInteger(r));
        }

        private static NexusValue SubVals(NexusValue l, NexusValue r)
        {
            if (l is NexusFloat || r is NexusFloat)
                return new NexusFloat(ValueHelpers.ToNumber(l) - ValueHelpers.ToNumber(r));
            return new NexusInt(ValueHelpers.ToInteger(l) - ValueHelpers.ToInteger(r));
        }

        private static NexusValue MulVals(NexusValue l, NexusValue r)
        {
            if (l is NexusFloat || r is NexusFloat)
                return new NexusFloat(ValueHelpers.ToNumber(l) * ValueHelpers.ToNumber(r));
            return new NexusInt(ValueHelpers.ToInteger(l) * ValueHelpers.ToInteger(r));
        }

        private static NexusValue DivVals(NexusValue l, NexusValue r)
        {
            var divisor = ValueHelpers.ToNumber(r);
            if (divisor == 0)
                throw new NexusRuntimeException("Division by zero.");
            return new NexusFloat(ValueHelpers.ToNumber(l) / divisor);
        }

        private static NexusValue ModVals(NexusValue l, NexusValue r)
        {
            var divisor = ValueHelpers.ToInteger(r);
            if (divisor == 0)
                throw new NexusRuntimeException("Modulo by zero.");
            return new NexusInt(ValueHelpers.ToInteger(l) % divisor);
        }

        private static NexusValue GetIndexed(NexusValue container, NexusValue index)
        {
            switch (container)
            {
                case NexusList list when index is NexusInt i:
                    return (i.Value >= 0 && i.Value < list.Items.Count)
                        ? list.Items[(int)i.Value] : NexusNull.Instance;
                case NexusDict dict when index is NexusString key:
                    return dict.Entries.TryGetValue(key.Value, out var val)
                        ? val : NexusNull.Instance;
                case NexusString str when index is NexusInt si:
                    return (si.Value >= 0 && si.Value < str.Value.Length)
                        ? new NexusChar(str.Value[(int)si.Value]) : NexusNull.Instance;
                default: return NexusNull.Instance;
            }
        }

        private static bool ValsEqual(NexusValue a, NexusValue b)
        {
            if (a is NexusNull && b is NexusNull) return true;
            if (a is NexusNull || b is NexusNull) return false;

            if (a is NexusEnum ea && b is NexusEnum eb)
                return ea.EnumName == eb.EnumName && ea.MemberName == eb.MemberName;

            if (a is NexusEnum || b is NexusEnum)
            {
                if (ValueHelpers.IsNumber(a) && ValueHelpers.IsNumber(b))
                    return ValueHelpers.ToNumber(a) == ValueHelpers.ToNumber(b);
                return false;
            }

            if (a.GetType() != b.GetType())
                return ValueHelpers.IsNumber(a) && ValueHelpers.IsNumber(b) &&
                       ValueHelpers.ToNumber(a) == ValueHelpers.ToNumber(b);
            return CompareVals(a, b) == 0;
        }

        private static int CompareVals(NexusValue a, NexusValue b)
        {
            if (ValueHelpers.IsNumber(a) && ValueHelpers.IsNumber(b))
            {
                var d = ValueHelpers.ToNumber(a) - ValueHelpers.ToNumber(b);
                return d == 0 ? 0 : d < 0 ? -1 : 1;
            }
            if (a is NexusString sa && b is NexusString sb)
                return string.CompareOrdinal(sa.Value, sb.Value);
            if (a is NexusChar ca && b is NexusChar cb) return ca.Value.CompareTo(cb.Value);
            if (a is NexusBool ab && b is NexusBool bb) return ab.Value.CompareTo(bb.Value);
            return ReferenceEquals(a, b) ? 0 : -1;
        }

        // ============ stack helpers ============
        /// <summary>Member-name operands may be stored as raw strings or as
        /// constant-pool indices — resolve either form.</summary>
        private static string ResolveStringOperand(object operand, BytecodeChunk chunk)
        {
            if (operand is string s) return s;
            if (operand is int idx && idx >= 0 && idx < chunk.ConstantPool.Count)
                return chunk.ConstantPool[idx] as string;
            return null;
        }

        private void Pop2(out NexusValue a, out NexusValue b)
        {
            b = _stack.Count > 0 ? _stack.Pop() : NexusNull.Instance;
            a = _stack.Count > 0 ? _stack.Pop() : NexusNull.Instance;
        }

        private long PopLong()
        {
            return _stack.Count > 0 ? ValueHelpers.ToInteger(_stack.Pop()) : 0;
        }

        private void BinaryLong(Func<long, long, long> op)
        {
            if (_stack.Count >= 2)
            {
                long b = PopLong();
                long a = PopLong();
                _stack.Push(new NexusInt(op(a, b)));
            }
        }

        // ============ ASM helpers ============
        private void HandleAsmMov(Instruction instr)
        {
            var dest = instr.Operand1?.ToString()?.ToLowerInvariant();
            var src = instr.Operand2?.ToString()?.ToLowerInvariant();

            if (dest == null) return;

            long value = 0;
            if (src != null)
            {
                if (_asmRegisters.ContainsKey(src))
                    value = _asmRegisters[src];
                else if (long.TryParse(src, out var parsed))
                    value = parsed;
                else if (src.StartsWith("["))
                {
                    var addrStr = src.Trim('[', ']');
                    if (long.TryParse(addrStr, out var addr) && addr >= 0 && addr < _memory.Count)
                        value = _memory[(int)addr];
                }
            }
            else if (_stack.Count > 0)
            {
                value = PopLong();
            }

            if (_asmRegisters.ContainsKey(dest))
                _asmRegisters[dest] = value;
        }

        private void HandleAsmBinaryOp(Instruction instr, Func<long, long, long> operation)
        {
            var dest = instr.Operand1?.ToString()?.ToLowerInvariant();
            var src = instr.Operand2?.ToString()?.ToLowerInvariant();

            if (dest == null || !_asmRegisters.ContainsKey(dest)) return;

            long srcValue = 0;
            if (src != null)
            {
                if (_asmRegisters.ContainsKey(src))
                    srcValue = _asmRegisters[src];
                else if (long.TryParse(src, out var parsed))
                    srcValue = parsed;
            }

            _asmRegisters[dest] = operation(_asmRegisters[dest], srcValue);
        }

        private void HandleAsmUnaryOp(Instruction instr, Func<long, long> operation)
        {
            var operand = instr.Operand1?.ToString()?.ToLowerInvariant();
            if (operand != null && _asmRegisters.ContainsKey(operand))
                _asmRegisters[operand] = operation(_asmRegisters[operand]);
        }

        private void HandleAsmCompare(Instruction instr)
        {
            var op1 = instr.Operand1?.ToString()?.ToLowerInvariant();
            var op2 = instr.Operand2?.ToString()?.ToLowerInvariant();

            long val1 = 0, val2 = 0;

            if (op1 != null)
            {
                if (_asmRegisters.ContainsKey(op1)) val1 = _asmRegisters[op1];
                else if (long.TryParse(op1, out var p1)) val1 = p1;
            }

            if (op2 != null)
            {
                if (_asmRegisters.ContainsKey(op2)) val2 = _asmRegisters[op2];
                else if (long.TryParse(op2, out var p2)) val2 = p2;
            }

            _lastComparison = val1.CompareTo(val2);
        }

        public string GetRegisterDump()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Register Dump ===");
            foreach (var reg in new[] { "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp" })
                sb.AppendLine($"  {reg.ToUpperInvariant(),-6} = 0x{_asmRegisters[reg]:X8} ({_asmRegisters[reg]})");
            sb.AppendLine($"  Last CMP: {_lastComparison}");
            sb.AppendLine($"  ASM Stack Depth: {_asmStack.Count}");
            return sb.ToString();
        }
    }
}

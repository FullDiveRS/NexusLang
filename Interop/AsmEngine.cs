using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Runtime;

namespace Nexus.Interop
{
    /// <summary>
    /// NASM-style x86 assembly engine simulating execution of Nexus `asm` blocks.
    ///
    /// - 32-bit registers with correct 16/8-bit sub-register aliasing
    /// - 1 MB simulated flat memory with a protected null page
    /// - push/pop hardware stack mirrored via ESP; call/ret via an int call stack
    /// - FLAGS (ZF/SF/CF/OF) driving full conditional jump set
    /// - data directives db/dw/dd/dq with NUL-terminated strings
    /// - memory operands: [eax], [eax+4], [label], [label+2], [eax+ebx*2], [esp]
    /// - bidirectional bridge with the Nexus runtime:
    ///     mov total, eax   → writes EAX into the Nexus variable `total`
    ///     mov eax, total   → reads the Nexus variable into EAX
    ///     call nexus_func  → invokes a Nexus function (fastcall ecx,edx,ebx,esi → eax)
    /// - instruction budget guards against infinite loops
    ///
    /// Not thread-safe — one engine per interpreter, like real registers.
    /// </summary>
    public class AsmEngine
    {
        // ================= MEMORY LAYOUT =================
        public const int MemorySize = 1024 * 1024;               // 1 MB
        public const int NullPageEnd = 0x1000;                   // page 0 protected
        public const int DataStart = 0x1100;                     // heap/data begins here
        public const int StackRegionSize = 64 * 1024;            // 64 KB stack
        public const int StackBase = MemorySize - StackRegionSize;
        public const int MaxInstructions = 1_000_000;            // infinite-loop guard

        // ================= REGISTERS =================
        private readonly Dictionary<string, long> _registers = new Dictionary<string, long>
        {
            ["eax"] = 0, ["ebx"] = 0, ["ecx"] = 0, ["edx"] = 0,
            ["esi"] = 0, ["edi"] = 0, ["ebp"] = 0, ["esp"] = StackBase
        };

        private static readonly Dictionary<string, Tuple<string, int, long>> RegisterAliases =
            new Dictionary<string, Tuple<string, int, long>>
            {
                ["ax"] = Tuple.Create("eax", 0, 0xFFFFL), ["bx"] = Tuple.Create("ebx", 0, 0xFFFFL),
                ["cx"] = Tuple.Create("ecx", 0, 0xFFFFL), ["dx"] = Tuple.Create("edx", 0, 0xFFFFL),
                ["al"] = Tuple.Create("eax", 0, 0xFFL),   ["bl"] = Tuple.Create("ebx", 0, 0xFFL),
                ["cl"] = Tuple.Create("ecx", 0, 0xFFL),   ["dl"] = Tuple.Create("edx", 0, 0xFFL),
                ["ah"] = Tuple.Create("eax", 8, 0xFFL),   ["bh"] = Tuple.Create("ebx", 8, 0xFFL),
                ["ch"] = Tuple.Create("ecx", 8, 0xFFL),   ["dh"] = Tuple.Create("edx", 8, 0xFFL),
            };

        private static readonly HashSet<string> Registers32 = new HashSet<string>
        {
            "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp"
        };

        // ================= STATE =================
        private readonly byte[] _memory = new byte[MemorySize];
        private readonly Stack<long> _dataStack = new Stack<long>();   // push/pop
        private readonly Stack<int> _callStack = new Stack<int>();     // call/ret return IPs
        private readonly Dictionary<string, long> _dataAddresses = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _variableAddresses = new Dictionary<string, long>();
        private readonly Dictionary<string, long> _stringCache = new Dictionary<string, long>();
        private readonly HashSet<string> _stringDataLabels = new HashSet<string>();
        private long _heapPointer = DataStart;
        private long _stackPointer = StackBase;

        private RuntimeEnvironment _env;
        private Interpreter _interpreter;

        // ================= FLAGS =================
        public bool ZeroFlag { get; private set; }
        public bool SignFlag { get; private set; }
        public bool CarryFlag { get; private set; }
        public bool OverflowFlag { get; private set; }

        // ================= CONFIG / DIAGNOSTICS =================
        /// <summary>When true, `mov new_name, eax` auto-creates a Nexus variable.</summary>
        public bool AllowAutoDefineVariables { get; set; } = true;
        public bool TraceEnabled { get; set; }
        public List<string> ExecutionLog { get; } = new List<string>();
        public int InstructionsExecuted { get; private set; }

        // ============================================================
        //  EXECUTION
        // ============================================================

        /// <summary>Executes a statement-level asm block against the environment.</summary>
        public void Execute(AsmBlock block, RuntimeEnvironment env, Interpreter interpreter)
        {
            _env = env;
            _interpreter = interpreter;

            foreach (var data in block.DataDefinitions)
                AllocateData(data);

            var program = new List<AsmInstruction>(block.Instructions);
            var labels = new Dictionary<string, int>(block.LabelPositions);

            int ip = 0;
            while (ip < program.Count)
            {
                if (++InstructionsExecuted > MaxInstructions)
                    throw new NexusRuntimeException(
                        $"ASM: exceeded {MaxInstructions} instructions — possible infinite loop " +
                        $"(asm block near line {block.Line}).");

                var inst = program[ip];
                if (TraceEnabled)
                    ExecutionLog.Add($"#{InstructionsExecuted,7}  [ip={ip,3}]  {inst}");

                int next = ExecuteInstruction(inst, labels, ip, program.Count);
                ip = next >= 0 ? next : ip + 1;
            }
        }

        private int ExecuteInstruction(AsmInstruction inst, Dictionary<string, int> labels,
                                       int ip, int programCount)
        {
            string m = inst.Mnemonic.ToLowerInvariant();
            string op1 = (inst.Operand1 ?? "").Trim();
            string op2 = (inst.Operand2 ?? "").Trim();
            const uint mask = 0xFFFFFFFF;

            switch (m)
            {
                // ==================== DATA MOVEMENT ====================
                case "mov":     // NOTE: mov does not modify FLAGS (matches x86)
                    StoreOperand(op1, ResolveOperand(op2));
                    return -1;

                case "xchg":
                {
                    long a = ResolveOperand(op1);
                    long b = ResolveOperand(op2);
                    StoreOperand(op1, b);
                    StoreOperand(op2, a);
                    return -1;
                }

                case "lea":
                {
                    long addr = op2.StartsWith("[") ? ResolveAddress(op2) : ResolveAddress($"[{op2}]");
                    StoreOperand(op1, addr);
                    return -1;
                }

                // ==================== ARITHMETIC ====================
                case "add":
                {
                    long a = ResolveOperand(op1);
                    long b = ResolveOperand(op2);
                    long r = (a + b) & mask;
                    StoreOperand(op1, r);
                    CarryFlag = (ulong)a + (ulong)b > uint.MaxValue;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "sub":
                {
                    long a = ResolveOperand(op1);
                    long b = ResolveOperand(op2);
                    long r = (a - b) & mask;
                    StoreOperand(op1, r);
                    CarryFlag = (ulong)a < (ulong)b;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "inc":
                {
                    long r = (ResolveOperand(op1) + 1) & mask;
                    StoreOperand(op1, r);
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "dec":
                {
                    long r = (ResolveOperand(op1) - 1) & mask;
                    StoreOperand(op1, r);
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "neg":
                {
                    long a = ResolveOperand(op1);
                    long r = (0 - a) & mask;
                    StoreOperand(op1, r);
                    CarryFlag = a != 0;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "mul":     // unsigned; imul matches at low 32 bits here
                case "imul":
                {
                    if (op2.Length == 0)
                    {
                        long r = GetRegister("eax") * ResolveOperand(op1);
                        SetRegister("eax", r & mask);
                        SetRegister("edx", (r >> 32) & mask);
                        UpdateFlagsResult(r & mask);
                    }
                    else
                    {
                        long r = (ResolveOperand(op1) * ResolveOperand(op2)) & mask;
                        StoreOperand(op1, r);
                        UpdateFlagsResult(r);
                    }
                    return -1;
                }

                case "div":
                {
                    if (op2.Length == 0)
                    {
                        long divisor = ResolveOperand(op1);
                        if (divisor == 0) throw new NexusRuntimeException("ASM: integer division by zero");
                        long dividend = GetRegister("eax");
                        SetRegister("eax", (dividend / divisor) & mask);
                        SetRegister("edx", (dividend % divisor) & mask);
                    }
                    else
                    {
                        long divisor = ResolveOperand(op2);
                        if (divisor == 0) throw new NexusRuntimeException("ASM: integer division by zero");
                        StoreOperand(op1, (ResolveOperand(op1) / divisor) & mask);
                    }
                    return -1;
                }

                case "idiv":
                {
                    if (op2.Length == 0)
                    {
                        long divisor = ToSigned(ResolveOperand(op1));
                        if (divisor == 0) throw new NexusRuntimeException("ASM: integer division by zero");
                        long dividend = ToSigned(GetRegister("eax"));
                        SetRegister("eax", dividend / divisor);
                        SetRegister("edx", dividend % divisor);
                    }
                    else
                    {
                        long divisor = ToSigned(ResolveOperand(op2));
                        if (divisor == 0) throw new NexusRuntimeException("ASM: integer division by zero");
                        StoreOperand(op1, ToSigned(ResolveOperand(op1)) / divisor);
                    }
                    return -1;
                }

                // ==================== BITWISE / LOGIC ====================
                case "and":
                {
                    long r = ResolveOperand(op1) & ResolveOperand(op2);
                    StoreOperand(op1, r);
                    CarryFlag = false;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "or":
                {
                    long r = ResolveOperand(op1) | ResolveOperand(op2);
                    StoreOperand(op1, r);
                    CarryFlag = false;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "xor":
                {
                    long r = ResolveOperand(op1) ^ ResolveOperand(op2);
                    StoreOperand(op1, r);
                    CarryFlag = false;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "not":     // does NOT affect FLAGS (matches x86)
                    StoreOperand(op1, ~ResolveOperand(op1) & mask);
                    return -1;

                case "shl":
                {
                    long a = ResolveOperand(op1);
                    int count = (int)(ResolveOperand(op2) & 31);
                    long r = count == 0 ? a : (a << count) & mask;
                    StoreOperand(op1, r);
                    CarryFlag = count > 0 && ((a >> (32 - count)) & 1) != 0;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "shr":
                {
                    long a = ResolveOperand(op1) & mask;   // logical (unsigned) shift
                    int count = (int)(ResolveOperand(op2) & 31);
                    long r = count == 0 ? a : a >> count;
                    StoreOperand(op1, r);
                    CarryFlag = count > 0 && ((a >> (count - 1)) & 1) != 0;
                    UpdateFlagsResult(r);
                    return -1;
                }

                case "sar":
                {
                    long a = ToSigned(ResolveOperand(op1)); // arithmetic (signed) shift
                    int count = (int)(ResolveOperand(op2) & 31);
                    long r = count == 0 ? a : a >> count;
                    StoreOperand(op1, r & mask);
                    UpdateFlagsResult(r & mask);
                    return -1;
                }

                // ==================== COMPARE / TEST ====================
                case "cmp":
                    UpdateFlagsCompare(ResolveOperand(op1), ResolveOperand(op2));
                    return -1;

                case "test":
                {
                    long r = ResolveOperand(op1) & ResolveOperand(op2);
                    ZeroFlag = r == 0;
                    SignFlag = (r & 0x80000000) != 0;
                    CarryFlag = false;
                    OverflowFlag = false;
                    return -1;
                }

                // ==================== STACK ====================
                case "push":
                {
                    long v = ResolveOperand(op1);
                    _dataStack.Push(v);
                    _stackPointer -= 8;
                    WriteMemoryLong(_stackPointer, v);
                    SetRegister("esp", _stackPointer);
                    return -1;
                }

                case "pop":
                {
                    if (_dataStack.Count == 0)
                        throw new NexusRuntimeException("ASM: pop from empty stack");
                    long v = _dataStack.Pop();
                    _stackPointer += 8;
                    StoreOperand(op1, v);
                    SetRegister("esp", _stackPointer);
                    return -1;
                }

                // ==================== CONTROL FLOW ====================
                case "jmp":
                    RequireOperand(inst, op1, "jmp");
                    return ResolveJumpTarget(op1, labels);

                case "call":
                {
                    RequireOperand(inst, op1, "call");

                    // 1) intra-block label → ordinary asm routine
                    if (labels.TryGetValue(op1, out int callTarget))
                    {
                        _callStack.Push(ip + 1);
                        return callTarget;
                    }

                    // 2) Nexus function (fastcall convention: ecx, edx, ebx, esi → eax)
                    if (_env != null && _interpreter != null &&
                        _env.TryGet(op1, out var callee) && callee is NexusFunction nf)
                    {
                        var args = new List<NexusValue>
                        {
                            new NexusInt(GetRegister("ecx")),
                            new NexusInt(GetRegister("edx")),
                            new NexusInt(GetRegister("ebx")),
                            new NexusInt(GetRegister("esi")),
                        };
                        var result = _interpreter.CallFunction(nf, args);
                        SetRegister("eax", ValueHelpers.ToInteger(result));
                        return -1;
                    }

                    throw new NexusRuntimeException(
                        $"ASM: call target '{op1}' is neither a label nor a Nexus function");
                }

                case "ret":
                    return _callStack.Count > 0 ? _callStack.Pop() : programCount;

                case "hlt":
                    return programCount;

                case "loop":
                {
                    long ecx = GetRegister("ecx") - 1;
                    SetRegister("ecx", ecx);
                    return ecx != 0 ? ResolveJumpTarget(op1, labels) : -1;
                }

                case "nop":
                    return -1;

                case "int":
                    // recognised but not simulated (no DOS/BIOS here!)
                    ExecutionLog.Add($"[int] software interrupt '{op1}' ignored");
                    return -1;

                default:
                    // conditional jumps: je, jne, jg, jl, jge, jle, ja, jb, jz, ...
                    if (m.StartsWith("j") && m != "jmp")
                    {
                        RequireOperand(inst, op1, m);
                        return ConditionMet(m) ? ResolveJumpTarget(op1, labels) : -1;
                    }
                    throw new NexusRuntimeException($"ASM: unknown instruction '{inst}'");
            }
        }

        // ============================================================
        //  ASM MODULES  (var m = asm { ... };  m.label(args))
        // ============================================================

        /// <summary>Allocates a module's db/dw/dd definitions into this engine's memory.</summary>
        public void LoadData(AsmBlock block)
        {
            foreach (var data in block.DataDefinitions)
                AllocateData(data);
        }

        /// <summary>
        /// Runs a module from a label until the matching top-level 'ret'/'hlt' or end
        /// of code. Calling convention: first 4 args → ECX, EDX, EBX, ESI (extras
        /// pushed on the asm stack). Returns EAX. Registers PERSIST across calls.
        /// </summary>
        public long InvokeLabel(AsmBlock block, string label, List<NexusValue> args,
                                RuntimeEnvironment env, Interpreter interpreter)
        {
            if (!block.LabelPositions.TryGetValue(label, out int startIp))
                throw new NexusRuntimeException(
                    $"ASM: module has no label '{label}' (labels: {string.Join(", ", block.Labels)})");

            _env = env;
            _interpreter = interpreter;
            _callStack.Clear();

            string[] argRegs = { "ecx", "edx", "ebx", "esi" };
            for (int i = 0; i < args.Count && i < argRegs.Length; i++)
                SetRegister(argRegs[i], ValueHelpers.ToInteger(args[i]));
            for (int i = args.Count - 1; i >= argRegs.Length; i--)
            {
                long v = ValueHelpers.ToInteger(args[i]);
                _dataStack.Push(v);
                _stackPointer -= 8;
                WriteMemoryLong(_stackPointer, v);
                SetRegister("esp", _stackPointer);
            }

            var program = block.Instructions;
            int ip = startIp, executed = 0;

            while (ip < program.Count)
            {
                if (++executed > MaxInstructions)
                    throw new NexusRuntimeException(
                        $"ASM: '{label}' exceeded {MaxInstructions} instructions — infinite loop?");

                int next = ExecuteInstruction(program[ip], block.LabelPositions, ip, program.Count);
                ip = next >= 0 ? next : ip + 1;   // top-level 'ret'/'hlt' returns programCount → ends
            }

            InstructionsExecuted += executed;
            return GetRegister("eax");
        }

        public NexusValue ReadDataLabel(string name)
        {
            if (!_dataAddresses.TryGetValue(name, out long addr))
                throw new NexusRuntimeException($"ASM: unknown data label '{name}'");
            return _stringDataLabels.Contains(name)
                ? new NexusString(ReadCString(addr))
                : new NexusInt(ReadMemoryQword(addr));
        }

        public void WriteDataLabel(string name, NexusValue value)
        {
            if (!_dataAddresses.TryGetValue(name, out long addr))
                throw new NexusRuntimeException(
                    $"ASM: cannot assign '{name}' — only db/dw/dd names declared in the module are settable");

            if (value is NexusString s)
            {
                long cursor = addr;
                foreach (char c in s.Value)
                    _memory[cursor++] = (byte)c;
                _memory[cursor] = 0;
                _stringDataLabels.Add(name);
            }
            else
            {
                WriteMemoryLong(addr, ValueHelpers.ToInteger(value));
            }
        }

        // ============================================================
        //  OPERAND RESOLUTION
        // ============================================================

        /// <summary>Reads the value of an operand: register, memory, label, variable, literal.</summary>
        private long ResolveOperand(string operand)
        {
            if (string.IsNullOrEmpty(operand))
                throw new NexusRuntimeException("ASM: missing operand");

            operand = operand.Trim();

            if (operand.StartsWith("["))
                return ReadMemoryQword(ResolveAddress(operand));

            if (IsRegister(operand))
                return GetRegister(operand);

            // like real asm: a data symbol IS its address (use [name] to dereference)
            if (_dataAddresses.TryGetValue(operand, out long dataAddr))
                return dataAddr;

            if (_env != null && _env.TryGet(operand, out var value))
            {
                // strings are interned into data memory and referenced by address,
                // exactly like a pointer to a db string in real asm
                if (value is NexusString s)
                    return InternString(s.Value);
                return ValueHelpers.ToInteger(value);
            }

            if (TryParseNumber(operand, out long number))
                return number;

            throw new NexusRuntimeException($"ASM: unknown operand '{operand}'");
        }

        /// <summary>Writes a value to an operand: register, memory, data label, or Nexus variable.</summary>
        private void StoreOperand(string operand, long value)
        {
            if (string.IsNullOrEmpty(operand))
                throw new NexusRuntimeException("ASM: missing destination operand");

            operand = operand.Trim();

            if (operand.StartsWith("["))
            {
                WriteMemoryLong(ResolveAddress(operand), value);
                return;
            }

            if (IsRegister(operand))
            {
                SetRegister(operand, value);
                return;
            }

            if (_dataAddresses.TryGetValue(operand, out long dataAddr))
            {
                WriteMemoryLong(dataAddr, value);
                return;
            }

            if (_env != null)
            {
                if (_env.TryGet(operand, out var existing))
                {
                    if (existing is NexusString)
                    {
                        // value is treated as a pointer to a NUL-terminated string
                        _env.Assign(operand, new NexusString(ReadCString(value)));
                    }
                    else
                    {
                        _env.Assign(operand, new NexusInt(value));
                    }
                    return;
                }

                if (AllowAutoDefineVariables)
                {
                    _env.Define(operand, new NexusInt(value));
                    return;
                }
            }

            throw new NexusRuntimeException($"ASM: cannot store to unknown destination '{operand}'");
        }

        // ============================================================
        //  MEMORY ADDRESSING  ([eax+4], [label], [eax+ebx*2], ...)
        // ============================================================

        private long ResolveAddress(string memRef)
        {
            string inner = memRef.Trim();
            if (inner.StartsWith("["))
                inner = inner.Trim('[', ']').Trim();

            long total = 0;
            long sign = 1;
            int i = 0;

            while (i < inner.Length)
            {
                while (i < inner.Length && inner[i] == ' ') i++;
                if (i >= inner.Length) break;

                if (inner[i] == '+') { sign = 1; i++; continue; }
                if (inner[i] == '-') { sign = -1; i++; continue; }

                int start = i;
                while (i < inner.Length &&
                       (char.IsLetterOrDigit(inner[i]) || inner[i] == '_' || inner[i] == '*'))
                    i++;

                string term = inner.Substring(start, i - start).Trim();
                if (term.Length == 0) break;

                total += sign * EvaluateTerm(term);
                sign = 1;
            }

            return total;
        }

        private long EvaluateTerm(string term)
        {
            var parts = term.Split('*');
            if (parts.Length == 2)
                return EvaluateAtomic(parts[0].Trim()) * EvaluateAtomic(parts[1].Trim());
            return EvaluateAtomic(term);
        }

        private long EvaluateAtomic(string atom)
        {
            if (IsRegister(atom))
                return GetRegister(atom);

            if (_dataAddresses.TryGetValue(atom, out long dataAddr))
                return dataAddr;

            // Nexus variables get a stable 16-byte address slot on first use
            if (_variableAddresses.TryGetValue(atom, out _) ||
                (_env != null && _env.IsDefined(atom)))
                return GetVariableAddress(atom, _env);

            if (TryParseNumber(atom, out long number))
                return number;

            throw new NexusRuntimeException($"ASM: unknown symbol '{atom}' in address expression");
        }

        // ============================================================
        //  REGISTERS (with sub-register aliasing)
        // ============================================================

        public static bool IsRegister(string name)
        {
            name = NormalizeReg(name);
            return Registers32.Contains(name) || RegisterAliases.ContainsKey(name);
        }

        public long GetRegister(string name)
        {
            name = NormalizeReg(name);
            if (_registers.TryGetValue(name, out long v))
                return v;
            if (RegisterAliases.TryGetValue(name, out var alias))
                return (_registers[alias.Item1] >> alias.Item2) & alias.Item3;
            return 0;
        }

        public void SetRegister(string name, long value)
        {
            name = NormalizeReg(name);
            if (_registers.ContainsKey(name))
            {
                _registers[name] = value & 0xFFFFFFFF;
                return;
            }
            if (RegisterAliases.TryGetValue(name, out var alias))
            {
                long parent = _registers[alias.Item1];
                long clearMask = ~(alias.Item3 << alias.Item2);
                _registers[alias.Item1] =
                    ((parent & clearMask) | ((value & alias.Item3) << alias.Item2)) & 0xFFFFFFFF;
                return;
            }
            throw new NexusRuntimeException($"ASM: unknown register '{name}'");
        }

        private static string NormalizeReg(string name)
        {
            name = name.Trim().ToLowerInvariant();
            return name.StartsWith("%") ? name.Substring(1) : name; // accept AT&T-style %eax
        }

        // ============================================================
        //  FLAGS
        // ============================================================

        private void UpdateFlagsResult(long result32)
        {
            ZeroFlag = result32 == 0;
            SignFlag = (result32 & 0x80000000) != 0;
        }

        private void UpdateFlagsCompare(long a, long b)
        {
            ZeroFlag = a == b;
            SignFlag = ToSigned(a) < ToSigned(b);   // signed less-than
            CarryFlag = (ulong)a < (ulong)b;        // unsigned below
            OverflowFlag = false;                    // simplified
        }

        private bool ConditionMet(string mnemonic)
        {
            switch (mnemonic)
            {
                // signed
                case "je": case "jz": return ZeroFlag;
                case "jne": case "jnz": return !ZeroFlag;
                case "jg": case "jnle": return !ZeroFlag && !SignFlag;
                case "jge": case "jnl": return !SignFlag;
                case "jl": case "jnge": return SignFlag;
                case "jle": case "jng": return SignFlag || ZeroFlag;
                case "js": return SignFlag;
                case "jns": return !SignFlag;

                // unsigned
                case "ja": case "jnbe": return !CarryFlag && !ZeroFlag;
                case "jae": case "jnb": return !CarryFlag;
                case "jb": case "jnae": return CarryFlag;
                case "jbe": case "jna": return CarryFlag || ZeroFlag;
                case "jc": return CarryFlag;
                case "jnc": return !CarryFlag;

                // overflow
                case "jo": return OverflowFlag;
                case "jno": return !OverflowFlag;

                default:
                    throw new NexusRuntimeException($"ASM: unknown conditional jump '{mnemonic}'");
            }
        }

        private int ResolveJumpTarget(string target, Dictionary<string, int> labels)
        {
            if (labels.TryGetValue(target, out int index))
                return index;
            throw new NexusRuntimeException($"ASM: undefined label '{target}'");
        }

        // ============================================================
        //  MEMORY
        // ============================================================

        /// <summary>Reads a 64-bit value (used by the Interpreter for `*ptr`).</summary>
        public long ReadMemory(long address) => ReadMemoryQword(address);

        /// <summary>Writes a 64-bit value.</summary>
        public void WriteMemory(long address, long value) => WriteMemoryLong(address, value);

        public long ReadMemoryQword(long address)
        {
            ValidateAddress(address, 8);
            long result = 0;
            for (int i = 7; i >= 0; i--)
                result = (result << 8) | _memory[address + i];
            return result;
        }

        public void WriteMemoryLong(long address, long value)
        {
            ValidateAddress(address, 8);
            for (int i = 0; i < 8; i++)
                _memory[address + i] = (byte)((value >> (i * 8)) & 0xFF);
        }

        /// <summary>Reads a NUL-terminated string starting at address.</summary>
        public string ReadCString(long address)
        {
            var sb = new System.Text.StringBuilder();
            long addr = address;
            while (addr >= 0 && addr < _memory.Length)
            {
                byte b = _memory[addr];
                if (b == 0) break;
                sb.Append((char)b);
                addr++;
            }
            return sb.ToString();
        }

        public string GetMemoryHexDump(long address, int length)
        {
            var sb = new System.Text.StringBuilder();
            for (int row = 0; row < length; row += 16)
            {
                sb.Append($"0x{address + row:X6}: ");
                for (int col = 0; col < 16 && address + row + col < _memory.Length; col++)
                    sb.Append($"{_memory[address + row + col]:X2} ");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private void ValidateAddress(long address, int size)
        {
            if (address < NullPageEnd)
                throw new NexusRuntimeException(
                    $"ASM: null-pointer access at 0x{address:X} (protected page)");
            if (address < 0 || address + size > _memory.Length)
                throw new NexusRuntimeException(
                    $"ASM: out-of-bounds memory access at 0x{address:X}");
        }

        // ============================================================
        //  DATA DIRECTIVES (db / dw / dd / dq)
        // ============================================================

        private void AllocateData(AsmDataDef data)
        {
            if (data.Values.Count == 0)
                return;

            long addr = ReserveHeap(EstimateSize(data));
            if (!string.IsNullOrEmpty(data.Name))
                _dataAddresses[data.Name] = addr;

            long cursor = addr;
            switch (data.Directive.ToLowerInvariant())
            {
                case "db":
                    bool hasString = false;
                    foreach (var v in data.Values)
                        if (v is string) { hasString = true; break; }
                    if (hasString && !string.IsNullOrEmpty(data.Name))
                        _stringDataLabels.Add(data.Name);

                    foreach (var v in data.Values)
                    {
                        if (v is string s)
                        {
                            foreach (char c in s)
                                _memory[cursor++] = (byte)c;
                            _memory[cursor++] = 0;   // NUL terminator
                        }
                        else
                        {
                            _memory[cursor++] = (byte)(Convert.ToInt64(v) & 0xFF);
                        }
                    }
                    break;

                case "dw":
                    foreach (var v in data.Values)
                        cursor = WriteUInt16(cursor, (ushort)(Convert.ToInt64(v) & 0xFFFF));
                    break;

                case "dd":
                    foreach (var v in data.Values)
                        cursor = WriteUInt32(cursor, (uint)(Convert.ToInt64(v) & 0xFFFFFFFF));
                    break;

                case "dq":
                    foreach (var v in data.Values)
                    {
                        WriteMemoryLong(cursor, Convert.ToInt64(v));
                        cursor += 8;
                    }
                    break;
            }
        }

        private long EstimateSize(AsmDataDef data)
        {
            int unit;
            switch (data.Directive.ToLowerInvariant())
            {
                case "dw": unit = 2; break;
                case "dd": unit = 4; break;
                case "dq": unit = 8; break;
                default: unit = 1; break;
            }

            long size = 0;
            foreach (var v in data.Values)
                size += v is string s ? s.Length + 1 : unit;
            return size + 8; // alignment headroom
        }

        private long ReserveHeap(long size)
        {
            long addr = _heapPointer;
            if (_heapPointer + size >= StackBase)
                throw new NexusRuntimeException("ASM: out of simulated memory (heap collided with stack)");
            _heapPointer += size;
            return addr;
        }

        private long WriteUInt16(long addr, ushort value)
        {
            _memory[addr] = (byte)(value & 0xFF);
            _memory[addr + 1] = (byte)((value >> 8) & 0xFF);
            return addr + 2;
        }

        private long WriteUInt32(long addr, uint value)
        {
            _memory[addr] = (byte)(value & 0xFF);
            _memory[addr + 1] = (byte)((value >> 8) & 0xFF);
            _memory[addr + 2] = (byte)((value >> 16) & 0xFF);
            _memory[addr + 3] = (byte)((value >> 24) & 0xFF);
            return addr + 4;
        }

        private long InternString(string s)
        {
            if (_stringCache.TryGetValue(s, out long cached))
                return cached;

            long addr = ReserveHeap(s.Length + 1);
            long cursor = addr;
            foreach (char c in s)
                _memory[cursor++] = (byte)c;
            _memory[cursor] = 0;

            _stringCache[s] = addr;
            return addr;
        }

        // ============================================================
        //  NEXUS VARIABLE BRIDGE (for the & address-of operator)
        // ============================================================

        public long GetVariableAddress(string name) => GetVariableAddress(name, _env);

        public long GetVariableAddress(string name, RuntimeEnvironment env)
        {
            if (_variableAddresses.TryGetValue(name, out long addr))
            {
                // keep the memory slot in sync so pointer reads see current values
                if (env != null && env.TryGet(name, out var cur) && !(cur is NexusString))
                    WriteMemoryLong(addr, ValueHelpers.ToInteger(cur));
                return addr;
            }

            addr = ReserveHeap(16);
            _variableAddresses[name] = addr;

            if (env != null && env.TryGet(name, out var value))
            {
                if (value is NexusString s)
                    WriteMemoryLong(addr, InternString(s.Value));
                else
                    WriteMemoryLong(addr, ValueHelpers.ToInteger(value));
            }
            return addr;
        }

        // ============================================================
        //  DIAGNOSTICS
        // ============================================================

        public string DumpRegisters()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== ASM Engine Register Dump ===");
            foreach (var reg in new[] { "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp" })
                sb.AppendLine($"  {reg.ToUpperInvariant(),-5} = 0x{GetRegister(reg):X8}  ({GetRegister(reg)})");
            sb.AppendLine($"  FLAGS: ZF={(ZeroFlag ? 1 : 0)} SF={(SignFlag ? 1 : 0)} " +
                          $"CF={(CarryFlag ? 1 : 0)} OF={(OverflowFlag ? 1 : 0)}");
            sb.AppendLine($"  Data stack depth: {_dataStack.Count}, Call stack depth: {_callStack.Count}");
            sb.AppendLine($"  Heap pointer: 0x{_heapPointer:X}");
            sb.AppendLine($"  Instructions executed: {InstructionsExecuted}");
            return sb.ToString();
        }

        public void Reset()
        {
            foreach (var key in new List<string>(_registers.Keys))
                _registers[key] = key == "esp" ? StackBase : 0;

            Array.Clear(_memory, 0, _memory.Length);
            _dataStack.Clear();
            _callStack.Clear();
            _dataAddresses.Clear();
            _variableAddresses.Clear();
            _stringCache.Clear();
            _stringDataLabels.Clear();
            _heapPointer = DataStart;
            _stackPointer = StackBase;
            ZeroFlag = SignFlag = CarryFlag = OverflowFlag = false;
            InstructionsExecuted = 0;
            ExecutionLog.Clear();
        }

        // ============================================================
        //  SMALL HELPERS
        // ============================================================

        private static long ToSigned(long v) => unchecked((int)v);   // sign-extend 32-bit

        private static void RequireOperand(AsmInstruction inst, string operand, string mnemonic)
        {
            if (string.IsNullOrEmpty(operand))
                throw new NexusRuntimeException(
                    $"ASM: '{mnemonic}' requires an operand (line {inst.Line})");
        }

        private static bool TryParseNumber(string text, out long value)
        {
            value = 0;
            text = text.Trim().ToLowerInvariant();

            try
            {
                if (text.StartsWith("0x") && text.Length > 2)
                { value = Convert.ToInt64(text.Substring(2), 16); return true; }

                if (text.StartsWith("0b") && text.Length > 2)
                { value = Convert.ToInt64(text.Substring(2), 2); return true; }

                if (text.StartsWith("0o") && text.Length > 2)
                { value = Convert.ToInt64(text.Substring(2), 8); return true; }

                // NASM hex suffix: 21h
                if (text.EndsWith("h") && text.Length > 1 && IsAllHexDigits(text.Substring(0, text.Length - 1)))
                { value = Convert.ToInt64(text.Substring(0, text.Length - 1), 16); return true; }

                // char literal: 'A'
                if (text.Length >= 3 && text.StartsWith("'") && text.EndsWith("'"))
                { value = text[1]; return true; }

                if (long.TryParse(text, out value))
                    return true;
            }
            catch
            {
                // fall through
            }

            return false;
        }

        private static bool IsAllHexDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }
}

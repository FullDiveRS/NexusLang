using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nexus.Core;
using Nexus.Runtime;

namespace Nexus.Bytecode
{
    public enum OpCode : byte
    {
        // Stack
        NOP = 0x00, POP = 0x01, DUP = 0x02, SWAP = 0x03,

        // Constants (LOAD_CONST_RAW pushes a NexusValue constant AS-IS,
        // e.g. a pre-built enum table, event or delegate type)
        LOAD_CONST_RAW = 0x0D, JMP_IF_NOT_NULL = 0x0E, CONCAT_N = 0x0F,
        LOAD_CONST = 0x10, LOAD_NULL = 0x11, LOAD_TRUE = 0x12, LOAD_FALSE = 0x13,
        LOAD_INT = 0x14,

        // Variables
        LOAD_VAR = 0x20, STORE_VAR = 0x21, LOAD_GLOBAL = 0x22, STORE_GLOBAL = 0x23,
        LOAD_LOCAL = 0x24, STORE_LOCAL = 0x25,

        // Arithmetic
        ADD = 0x30, SUB = 0x31, MUL = 0x32, DIV = 0x33, MOD = 0x34,
        NEG = 0x35, INC = 0x36, DEC = 0x37,

        // Bitwise
        AND = 0x38, OR = 0x39, XOR = 0x3A, NOT = 0x3B, SHL = 0x3C, SHR = 0x3D,

        // Comparison
        EQ = 0x40, NEQ = 0x41, LT = 0x42, GT = 0x43, LTE = 0x44, GTE = 0x45,

        // Logical
        LOGICAL_AND = 0x48, LOGICAL_OR = 0x49, LOGICAL_NOT = 0x4A,

        // Control flow
        JMP = 0x50, JMP_IF_FALSE = 0x51, JMP_IF_TRUE = 0x52, LOOP = 0x53,

        // Functions
        CALL = 0x60, CALL_NATIVE = 0x61, RETURN = 0x62, CALL_INTEROP = 0x63,
        CALL_SUPER = 0x64, TRY_PUSH = 0x65, TRY_POP = 0x66,

        // Data structures
        NEW_LIST = 0x70, NEW_DICT = 0x71, NEW_ARRAY = 0x72, NEW_OBJECT = 0x73,
        LIST_APPEND = 0x74, LIST_INDEX = 0x75, LIST_SET = 0x76,
        DICT_SET = 0x77, DICT_GET = 0x78, DICT_KEYS = 0x7F,

        // Member access
        GET_MEMBER = 0x80, SET_MEMBER = 0x81, GET_INDEX = 0x82, SET_INDEX = 0x83,

        // NASM/ASM
        ASM_MOV = 0x90, ASM_PUSH = 0x91, ASM_POP = 0x92,
        ASM_ADD = 0x93, ASM_SUB = 0x94, ASM_MUL = 0x95, ASM_DIV = 0x96,
        ASM_INC = 0x97, ASM_DEC = 0x98, ASM_CMP = 0x99,
        ASM_JMP = 0x9A, ASM_JE = 0x9B, ASM_JNE = 0x9C, ASM_JG = 0x9D,
        ASM_JL = 0x9E, ASM_JGE = 0x9F, ASM_JLE = 0xA0,
        ASM_CALL = 0xA1, ASM_RET = 0xA2,
        ASM_AND = 0xA3, ASM_OR = 0xA4, ASM_XOR = 0xA5, ASM_NOT = 0xA6,
        ASM_SHL = 0xA7, ASM_SHR = 0xA8,
        ASM_LOAD_REG = 0xA9, ASM_STORE_REG = 0xAA, ASM_LABEL = 0xAB,
        ASM_NOP = 0xAC, ASM_LEA = 0xAD, ASM_TEST = 0xAE,

        // Types
        TYPE_CHECK = 0xB0, TYPE_CAST = 0xB1,

        // Classes
        CLASS_DEFINE = 0xC0, FIELD_GET = 0xC1, FIELD_SET = 0xC2,
        METHOD_CALL = 0xC3, NEW_INSTANCE = 0xC4,

        // Exceptions
        THROW = 0xD0, TRY_SETUP = 0xD1, TRY_CLEANUP = 0xD2,

        // Output
        PRINT = 0xE0, PRINTLN = 0xE1,

        // Debug
        BREAKPOINT = 0xF0, HALT = 0xFF
    }

    public class Instruction
    {
        public OpCode Op { get; }
        public object Operand1 { get; set; }
        public object Operand2 { get; set; }
        public int Line { get; }
        public int Offset { get; set; }

        public Instruction(OpCode op, object op1 = null, object op2 = null, int line = 0)
        {
            Op = op; Operand1 = op1; Operand2 = op2; Line = line;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Op);
            if (Operand1 != null) sb.Append($" {Operand1}");
            if (Operand2 != null) sb.Append($", {Operand2}");
            return sb.ToString();
        }
    }

    public class BytecodeChunk
    {
        public string Name { get; set; } = "main";
        public List<Instruction> Instructions { get; } = new List<Instruction>();
        public List<object> ConstantPool { get; } = new List<object>();
        public Dictionary<string, int> LabelMap { get; } = new Dictionary<string, int>();
        public int MaxStackSize { get; set; }
        public int NumLocals { get; set; }

        /// <summary>Slot index for each declared parameter, in order. When set,
        /// the VM binds call arguments by slot (not positionally), which keeps
        /// argument mapping correct even when slots were allocated out of order.</summary>
        public List<int> ParamSlots { get; } = new List<int>();

        /// <summary>Slot of `this` for instance methods / property accessors.
        /// Null for plain functions.</summary>
        public int? ThisSlot { get; set; }

        public int AddConstant(object value)
        {
            for (int i = 0; i < ConstantPool.Count; i++)
            {
                if (Equals(ConstantPool[i], value))
                    return i;
            }
            ConstantPool.Add(value);
            return ConstantPool.Count - 1;
        }

        public void AddInstruction(Instruction instr)
        {
            instr.Offset = Instructions.Count;
            Instructions.Add(instr);
        }

        public string Disassemble()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== Bytecode: {Name} ===");
            sb.AppendLine($"Constants ({ConstantPool.Count}):");
            for (int i = 0; i < ConstantPool.Count; i++)
                sb.AppendLine($"  [{i}] {ConstantPool[i]} ({ConstantPool[i]?.GetType().Name})");
            sb.AppendLine();
            sb.AppendLine($"Instructions ({Instructions.Count}):");
            sb.AppendLine($"{"Offset",-6} {"Opcode",-18} Operands");
            sb.AppendLine(new string('-', 60));

            foreach (var instr in Instructions)
            {
                var operands = "";
                if (instr.Operand1 != null) operands += instr.Operand1;
                if (instr.Operand2 != null) operands += $", {instr.Operand2}";
                sb.AppendLine($"{instr.Offset,-6} {instr.Op,-18} {operands}");
            }

            if (LabelMap.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Labels:");
                foreach (var kvp in LabelMap)
                    sb.AppendLine($"  {kvp.Key}: @{kvp.Value}");
            }

            return sb.ToString();
        }

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(Encoding.UTF8.GetBytes("NEXUS_BC"));
                writer.Write((byte)2);
                writer.Write(Name ?? "");

                // parameter slots + this-slot (needed for call conventions)
                writer.Write(ParamSlots.Count);
                foreach (var slot in ParamSlots)
                    writer.Write(slot);
                writer.Write(ThisSlot.HasValue);
                if (ThisSlot.HasValue) writer.Write(ThisSlot.Value);

                writer.Write(ConstantPool.Count);
                foreach (var constant in ConstantPool)
                    WriteConstant(writer, constant);

                writer.Write(Instructions.Count);
                foreach (var instr in Instructions)
                {
                    writer.Write((byte)instr.Op);
                    WriteOperand(writer, instr.Operand1);
                    WriteOperand(writer, instr.Operand2);
                }

                return ms.ToArray();
            }
        }

        /// <summary>Inverse of Serialize — validates the "NEXUS_BC" magic and
        /// version, then rebuilds name, call-convention slots, constants and
        /// instructions. Throws InvalidDataException on a malformed file.</summary>
        public static BytecodeChunk Deserialize(byte[] data)
        {
            using (var ms = new MemoryStream(data ?? throw new InvalidDataException("Empty bytecode")))
            using (var reader = new BinaryReader(ms))
            {
                var magic = Encoding.UTF8.GetString(reader.ReadBytes(8));
                if (magic != "NEXUS_BC")
                    throw new InvalidDataException($"Bad bytecode magic '{magic}' (expected NEXUS_BC)");

                byte version = reader.ReadByte();
                if (version != 2)
                    throw new InvalidDataException($"Unsupported bytecode version {version} (expected 2)");

                var chunk = new BytecodeChunk { Name = reader.ReadString() };

                int paramCount = reader.ReadInt32();
                for (int i = 0; i < paramCount; i++)
                    chunk.ParamSlots.Add(reader.ReadInt32());
                if (reader.ReadBoolean())
                    chunk.ThisSlot = reader.ReadInt32();

                int constCount = reader.ReadInt32();
                for (int i = 0; i < constCount; i++)
                    chunk.ConstantPool.Add(ReadConstant(reader));

                int instrCount = reader.ReadInt32();
                for (int i = 0; i < instrCount; i++)
                {
                    var op = (OpCode)reader.ReadByte();
                    var instr = new Instruction(op, ReadOperand(reader), ReadOperand(reader));
                    instr.Offset = chunk.Instructions.Count;
                    chunk.Instructions.Add(instr);
                }

                return chunk;
            }
        }

        private static object ReadOperand(BinaryReader reader)
        {
            switch (reader.ReadByte())
            {
                case 1: return reader.ReadInt32();
                case 2: return reader.ReadInt64();
                case 3: return reader.ReadString();
                case 4: return reader.ReadDouble();
                case 5: return reader.ReadString();
                default: return null;
            }
        }

        /// <summary>Multi-chunk container: "NEXUS_BC2" + count + length-prefixed
        /// chunks. The compiler emits one chunk per function/class member, so a
        /// runnable program needs them all.</summary>
        public static void SavePackage(Dictionary<string, BytecodeChunk> chunks, string path)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(Encoding.UTF8.GetBytes("NEXUS_BC2"));
                writer.Write(chunks.Count);
                foreach (var chunk in chunks.Values)
                {
                    var bytes = chunk.Serialize();
                    writer.Write(bytes.Length);
                    writer.Write(bytes);
                }
                File.WriteAllBytes(path, ms.ToArray());
            }
        }

        public static Dictionary<string, BytecodeChunk> LoadPackage(string path)
        {
            var result = new Dictionary<string, BytecodeChunk>();
            var pending = new List<Tuple<NexusClass, string>>();
            var savedLinks = _pendingBaseLinks;
            _pendingBaseLinks = pending;

            try
            {
                var data = File.ReadAllBytes(path);
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    var magic = Encoding.UTF8.GetString(reader.ReadBytes(9));
                    if (magic != "NEXUS_BC2")
                        throw new InvalidDataException($"Bad bytecode package magic '{magic}'");

                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        int length = reader.ReadInt32();
                        var bytes = reader.ReadBytes(length);
                        var chunk = Deserialize(bytes);
                        result[chunk.Name] = chunk;
                    }
                }
            }
            finally
            {
                _pendingBaseLinks = savedLinks;
            }

            // link base classes now that every class metadata exists
            var classes = result.Values.SelectMany(c => c.ConstantPool)
                                .OfType<NexusClass>().ToList();
            foreach (var (cls, baseName) in pending)
            {
                if (!string.IsNullOrEmpty(baseName))
                    cls.BaseClass = classes.FirstOrDefault(c => c.Name == baseName);
            }

            return result;
        }

        private static void WriteConstant(BinaryWriter writer, object value)
        {
            // tags 0-5 are the original primitives; 6+ are Nexus runtime values
            // so enum tables, events and class metadata survive a .nbc round-trip
            switch (value)
            {
                case null: writer.Write((byte)0); break;
                case long l: writer.Write((byte)1); writer.Write(l); break;
                case double d: writer.Write((byte)2); writer.Write(d); break;
                case string s: writer.Write((byte)3); writer.Write(s); break;
                case bool b: writer.Write((byte)4); writer.Write(b); break;
                case NexusInt ni: writer.Write((byte)6); writer.Write(ni.Value); break;
                case NexusFloat nf: writer.Write((byte)7); writer.Write(nf.Value); break;
                case NexusBool nb: writer.Write((byte)8); writer.Write(nb.Value); break;
                case NexusString ns: writer.Write((byte)9); writer.Write(ns.Value); break;
                case NexusNull: writer.Write((byte)10); break;
                case NexusEnum ne:
                    writer.Write((byte)11);
                    writer.Write(ne.EnumName ?? ""); writer.Write(ne.MemberName ?? "");
                    writer.Write(ne.Value);
                    break;
                case NexusFunction fn:   // placeholder whose Name is a chunk name
                    writer.Write((byte)12); writer.Write(fn.Name ?? ""); break;
                case NexusEvent ev:
                    writer.Write((byte)13);
                    writer.Write(ev.Name ?? ""); writer.Write(ev.DelegateType ?? "");
                    break;
                case NexusDict dict:
                    writer.Write((byte)14); writer.Write(dict.Entries.Count);
                    foreach (var kvp in dict.Entries)
                    { writer.Write(kvp.Key); WriteConstant(writer, kvp.Value); }
                    break;
                case NexusList list:
                    writer.Write((byte)15); writer.Write(list.Items.Count);
                    foreach (var item in list.Items)
                        WriteConstant(writer, item);
                    break;
                case NexusClass cls:
                    WriteClassMeta(writer, cls);
                    break;
                case Nexus.Interop.NexusInteropFunction iof:
                    writer.Write((byte)17);
                    writer.Write(iof.InteropType ?? "");
                    writer.Write(iof.Library ?? "");
                    writer.Write(iof.EntryPoint ?? "");
                    break;
                default:
                    writer.Write((byte)5); writer.Write(value?.ToString() ?? ""); break;
            }
        }

        private static void WriteClassMeta(BinaryWriter writer, NexusClass cls)
        {
            writer.Write((byte)16);
            writer.Write(cls.Name ?? "");
            writer.Write(cls.IsEnum);
            writer.Write(cls.BaseClass?.Name ?? "");

            // instance field NAMES in declaration order (values are rebuilt at
            // runtime from the __fields_<cls> chunk, so initializers are not needed)
            writer.Write(cls.FieldDeclarations.Count);
            foreach (var fd in cls.FieldDeclarations)
                if (fd is VariableDecl vd) writer.Write(vd.Name);

            writer.Write(cls.Methods.Count);
            foreach (var m in cls.Methods)
            { writer.Write(m.Key); writer.Write((m.Value as NexusFunction)?.Name ?? ""); }

            writer.Write(cls.StaticFields.Count);
            foreach (var sf in cls.StaticFields)
            { writer.Write(sf.Key); WriteConstant(writer, sf.Value); }

            writer.Write(cls.Properties.Count);
            foreach (var p in cls.Properties)
            {
                writer.Write(p.Key);
                writer.Write(p.Value.Getter != null);
                writer.Write(p.Value.GetterIsAuto);
                writer.Write(p.Value.Getter?.Name ?? "");
                writer.Write(p.Value.Setter != null);
                writer.Write(p.Value.SetterIsAuto);
                writer.Write(p.Value.Setter?.Name ?? "");
            }

            writer.Write(cls.Operators.Count);
            foreach (var o in cls.Operators)
            { writer.Write(o.Key); writer.Write(o.Value?.Name ?? ""); }

            writer.Write(cls.EventNames.Count);
            foreach (var e in cls.EventNames)
                writer.Write(e);
        }

        private static object ReadConstant(BinaryReader reader)
        {
            switch (reader.ReadByte())
            {
                case 1: return reader.ReadInt64();
                case 2: return reader.ReadDouble();
                case 3: return reader.ReadString();
                case 4: return reader.ReadBoolean();
                case 5: return reader.ReadString();
                case 6: return new NexusInt(reader.ReadInt64());
                case 7: return new NexusFloat(reader.ReadDouble());
                case 8: return new NexusBool(reader.ReadBoolean());
                case 9: return new NexusString(reader.ReadString());
                case 10: return NexusNull.Instance;
                case 11: return new NexusEnum(reader.ReadString(), reader.ReadString(), reader.ReadInt64());
                case 12: return new NexusFunction(reader.ReadString());
                case 13: return new NexusEvent(reader.ReadString(), reader.ReadString());
                case 14:
                {
                    var dict = new NexusDict();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        var key = reader.ReadString();
                        dict.Entries[key] = (NexusValue)ReadConstant(reader);
                    }
                    return dict;
                }
                case 15:
                {
                    var list = new NexusList();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                        list.Items.Add((NexusValue)ReadConstant(reader));
                    return list;
                }
                case 16: return ReadClassMeta(reader);
                case 17: return new Nexus.Interop.NexusInteropFunction(
                             reader.ReadString(), reader.ReadString(), reader.ReadString());
                default: return null;
            }
        }

        private static NexusClass ReadClassMeta(BinaryReader reader)
        {
            var cls = new NexusClass(reader.ReadString()) { IsEnum = reader.ReadBoolean() };
            var baseName = reader.ReadString();
            int fieldCount = reader.ReadInt32();
            for (int i = 0; i < fieldCount; i++)
                cls.FieldDeclarations.Add(new VariableDecl(reader.ReadString(), "dynamic", "var", null));

            int methodCount = reader.ReadInt32();
            for (int i = 0; i < methodCount; i++)
                cls.Methods[reader.ReadString()] = new NexusFunction(reader.ReadString());

            int staticCount = reader.ReadInt32();
            for (int i = 0; i < staticCount; i++)
                cls.StaticFields[reader.ReadString()] = (NexusValue)ReadConstant(reader);

            int propCount = reader.ReadInt32();
            for (int i = 0; i < propCount; i++)
            {
                var name = reader.ReadString();
                bool hasGetter = reader.ReadBoolean();
                bool getterAuto = reader.ReadBoolean();
                var getterName = reader.ReadString();
                bool hasSetter = reader.ReadBoolean();
                bool setterAuto = reader.ReadBoolean();
                var setterName = reader.ReadString();
                cls.Properties[name] = new PropertyAccessors
                {
                    Name = name,
                    GetterIsAuto = getterAuto,
                    Getter = hasGetter ? new NexusFunction(getterName) : null,
                    SetterIsAuto = setterAuto,
                    Setter = hasSetter ? new NexusFunction(setterName) : null
                };
            }

            int opCount = reader.ReadInt32();
            for (int i = 0; i < opCount; i++)
                cls.Operators[reader.ReadString()] = new NexusFunction(reader.ReadString());

            int eventCount = reader.ReadInt32();
            for (int i = 0; i < eventCount; i++)
                cls.EventNames.Add(reader.ReadString());

            // BaseClass is resolved by LoadPackage once every class exists
            _pendingBaseLinks?.Add(Tuple.Create(cls, baseName));
            return cls;
        }

        /// <summary>(class, baseName) pairs gathered while deserializing class
        /// metadata; LoadPackage consumes and clears it. Thread-local so nested
        /// loads never interfere.</summary>
        [ThreadStatic]
        private static List<Tuple<NexusClass, string>> _pendingBaseLinks;

        private static void WriteOperand(BinaryWriter writer, object operand)
        {
            switch (operand)
            {
                case null: writer.Write((byte)0); break;
                case int i: writer.Write((byte)1); writer.Write(i); break;
                case long l: writer.Write((byte)2); writer.Write(l); break;
                case string s: writer.Write((byte)3); writer.Write(s); break;
                case double d: writer.Write((byte)4); writer.Write(d); break;
                default: writer.Write((byte)5); writer.Write(operand?.ToString() ?? ""); break;
            }
        }
    }
}

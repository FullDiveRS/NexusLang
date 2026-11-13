using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Core;
using Nexus.Runtime;

namespace Nexus.Interop
{
    /// <summary>A compiled asm block held in a variable. Instruction labels are
    /// callable functions; db/dw/dd data labels are readable/writable properties.</summary>
    public class NexusAsmModule : NexusValue
    {
        public string Name { get; set; }
        public AsmBlock Block { get; }
        public AsmEngine Engine { get; }               // isolated per module
        public RuntimeEnvironment CapturedEnv { get; } // for mov var, eax bridging
        private readonly Interpreter _interpreter;

        public NexusAsmModule(string name, AsmBlock block, AsmEngine engine,
                              RuntimeEnvironment env, Interpreter interpreter)
        {
            Name = name; Block = block; Engine = engine;
            CapturedEnv = env; _interpreter = interpreter;
        }

        public override string TypeName => $"asm:{Name}";
        public override string ToString() =>
            $"<asm module {Name}: {Block.Labels.Count} labels, {Block.Instructions.Count} instructions>";

        public bool IsLabel(string name) => Block.LabelPositions.ContainsKey(name);
        public bool IsData(string name) => Block.DataDefinitions.Any(d => d.Name == name);

        public NexusValue GetMember(string name)
        {
            if (IsLabel(name)) return new NexusAsmLabelFunction(this, name);
            if (IsData(name)) return Engine.ReadDataLabel(name);

            if (name == "labels")
                return new NexusList(Block.Labels.Select(l => (NexusValue)new NexusString(l)).ToList());
            if (name == "data")
                return new NexusList(Block.DataDefinitions
                    .Where(d => !string.IsNullOrEmpty(d.Name))
                    .Select(d => (NexusValue)new NexusString(d.Name)).ToList());

            var dataList = Block.DataDefinitions
                .Where(d => !string.IsNullOrEmpty(d.Name))
                .Select(d => d.Name);

            throw new NexusRuntimeException(
                $"'{Name}.{name}' not found — labels: [{string.Join(", ", Block.Labels)}], " +
                $"data: [{string.Join(", ", dataList)}]");
        }

        public void SetMember(string name, NexusValue value) => Engine.WriteDataLabel(name, value);

        public string DefaultEntryPoint()
        {
            foreach (var c in new[] { "main", "start", "_start" })
                if (IsLabel(c)) return c;
            return Block.Labels.Count > 0 ? Block.Labels[0] : "";
        }
    }

    /// <summary>A single label of an asm module, callable as a function.</summary>
    public class NexusAsmLabelFunction : NexusValue
    {
        public NexusAsmModule Module { get; }
        public string Label { get; }

        public NexusAsmLabelFunction(NexusAsmModule module, string label)
        {
            Module = module; Label = label;
        }

        public override string TypeName => "asmfn";
        public override string ToString() => $"<asm fn {Module.Name}.{Label}>";
    }
}

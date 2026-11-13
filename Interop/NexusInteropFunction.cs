using System;
using System.Collections.Generic;
using Nexus.Core;
using Nexus.Runtime;

namespace Nexus.Interop
{
    /// <summary>Wraps a function declared via an interop attribute
    /// (DllImport, NasmImport, JavaImport, GoImport, CSharpImport, etc.).</summary>
    public class NexusInteropFunction : NexusValue
    {
        public string InteropType { get; set; }
        public string Library { get; set; }
        public string EntryPoint { get; set; }
        public string ReturnType { get; set; } = "void";
        public List<Parameter> Parameters { get; set; } = new List<Parameter>();
        public Delegate NativeDelegate { get; set; }

        public NexusInteropFunction(string interopType, string library, string entryPoint)
        {
            InteropType = interopType;
            Library = library;
            EntryPoint = entryPoint;
        }

        public override string TypeName => $"interop:{InteropType}";
        public override string ToString() => $"<interop {InteropType}:{EntryPoint}@{Library}>";
    }
}

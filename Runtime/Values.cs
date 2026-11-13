using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nexus.Core;
using Nexus.Interop;

namespace Nexus.Runtime
{
    public abstract class NexusValue
    {
        public abstract override string ToString();
        public abstract string TypeName { get; }
        public virtual bool IsTruthy() => true;
    }

    public class NexusInt : NexusValue
    {
        public long Value { get; set; }
        public NexusInt(long value) { Value = value; }
        public override string TypeName => "int";
        public override string ToString() => Value.ToString();
        public override bool IsTruthy() => Value != 0;
    }

    public class NexusFloat : NexusValue
    {
        public double Value { get; set; }
        public NexusFloat(double value) { Value = value; }
        public override string TypeName => "float";
        public override string ToString() => Value.ToString("G17");
        public override bool IsTruthy() => Value != 0.0;
    }

    public class NexusString : NexusValue
    {
        public string Value { get; set; }
        public NexusString(string value) { Value = value ?? ""; }
        public override string TypeName => "string";
        public override string ToString() => Value;
        public override bool IsTruthy() => !string.IsNullOrEmpty(Value);
    }

    public class NexusBool : NexusValue
    {
        public bool Value { get; set; }
        public NexusBool(bool value) { Value = value; }
        public override string TypeName => "bool";
        public override string ToString() => Value ? "true" : "false";
        public override bool IsTruthy() => Value;
    }

    public class NexusNull : NexusValue
    {
        public static readonly NexusNull Instance = new NexusNull();
        public override string TypeName => "null";
        public override string ToString() => "null";
        public override bool IsTruthy() => false;
    }

    public class NexusChar : NexusValue
    {
        public char Value { get; set; }
        public NexusChar(char value) { Value = value; }
        public override string TypeName => "char";
        public override string ToString() => Value.ToString();
        public override bool IsTruthy() => Value != '\0';
    }

    public class NexusList : NexusValue
    {
        public List<NexusValue> Items { get; } = new List<NexusValue>();
        public NexusList() { }
        public NexusList(List<NexusValue> items) { Items.AddRange(items ?? new List<NexusValue>()); }
        public override string TypeName => "list";
        public override string ToString()
        {
            var sb = new StringBuilder("[");
            sb.Append(string.Join(", ", Items.Select(i => i.ToString())));
            sb.Append("]");
            return sb.ToString();
        }
        public override bool IsTruthy() => Items.Count > 0;
    }

    public class NexusDict : NexusValue
    {
        public Dictionary<string, NexusValue> Entries { get; } = new Dictionary<string, NexusValue>();
        public override string TypeName => "dict";
        public override string ToString()
        {
            var sb = new StringBuilder("{");
            sb.Append(string.Join(", ", Entries.Select(kv => $"{kv.Key}: {kv.Value}")));
            sb.Append("}");
            return sb.ToString();
        }
        public override bool IsTruthy() => Entries.Count > 0;
    }

    public class NexusArray : NexusValue
    {
        public NexusValue[] Elements { get; }
        public NexusArray(NexusValue[] elements) { Elements = elements ?? new NexusValue[0]; }
        public override string TypeName => "array";
        public override string ToString()
        {
            var sb = new StringBuilder("[");
            sb.Append(string.Join(", ", Elements.Select(e => e.ToString())));
            sb.Append("]");
            return sb.ToString();
        }
        public override bool IsTruthy() => Elements.Length > 0;
    }

    public class NexusFunction : NexusValue
    {
        public string Name { get; set; }
        public List<Parameter> Parameters { get; set; } = new List<Parameter>();
        public List<Statement> Body { get; set; } = new List<Statement>();
        public RuntimeEnvironment Closure { get; set; }
        public bool IsNative { get; set; }
        public Func<List<NexusValue>, NexusValue> NativeImplementation { get; set; }
        public string ReturnType { get; set; }

        /// <summary>Instance binding: when set, `this` is defined in the call scope
        /// (methods obtained through an instance get this filled in).</summary>
        public NexusValue BoundThis { get; set; }

        public NexusFunction(string name)
        {
            Name = name;
        }

        public override string TypeName => "function";
        public override string ToString() => $"<function {Name}>";
    }

    public class NexusClass : NexusValue
    {
        public string Name { get; set; }
        public NexusClass BaseClass { get; set; }
        public Dictionary<string, NexusValue> Methods { get; } = new Dictionary<string, NexusValue>();
        public Dictionary<string, NexusValue> StaticFields { get; } = new Dictionary<string, NexusValue>();
        public List<Statement> FieldDeclarations { get; } = new List<Statement>();

        /// <summary>Properties: name → (getter, setter) accessor functions.
        /// Function.Body empty = auto-implemented (backed by the instance field).</summary>
        public Dictionary<string, PropertyAccessors> Properties { get; } =
            new Dictionary<string, PropertyAccessors>();

        /// <summary>Operator overloads keyed by "<symbol>#<arity>", e.g. "+#2",
        /// "-#1" (unary), "==#2".</summary>
        public Dictionary<string, NexusFunction> Operators { get; } =
            new Dictionary<string, NexusFunction>();

        /// <summary>Instance event declarations — each instance gets its own
        /// NexusEvent field with this name at construction.</summary>
        public List<string> EventNames { get; } = new List<string>();

        /// <summary>Live static scope: static fields AND methods are visible here,
        /// so mutations by static methods are seen by external reads.</summary>
        public RuntimeEnvironment StaticEnv { get; set; }

        /// <summary>Enum-flavored class (created by `enum` with members).</summary>
        public bool IsEnum { get; set; }

        public NexusClass(string name)
        {
            Name = name;
        }

        public override string TypeName => IsEnum ? $"enum::{Name}" : "class";
        public override string ToString() => IsEnum ? $"<enum {Name}>" : $"<class {Name}>";

        public NexusValue FindMethod(string name)
        {
            if (Methods.TryGetValue(name, out var method))
                return method;
            return BaseClass != null ? BaseClass.FindMethod(name) : NexusNull.Instance;
        }

        public PropertyAccessors FindProperty(string name)
        {
            if (Properties.TryGetValue(name, out var prop))
                return prop;
            return BaseClass?.FindProperty(name);
        }

        /// <summary>Find an operator overload walking the base-class chain.</summary>
        public NexusFunction FindOperator(string symbol, int arity)
        {
            if (Operators.TryGetValue($"{symbol}#{arity}", out var op))
                return op;
            return BaseClass?.FindOperator(symbol, arity);
        }
    }

    /// <summary>Getter/setter pair for a property. Getter takes no arguments;
    /// setter takes one (`value`).</summary>
    public class PropertyAccessors
    {
        public string Name { get; set; }
        public NexusFunction Getter { get; set; }   // null = no getter
        public NexusFunction Setter { get; set; }   // null = no setter
        public bool GetterIsAuto { get; set; }      // no body → read backing field
        public bool SetterIsAuto { get; set; }      // no body → write backing field
    }

    /// <summary>A multicast event: an ordered list of subscribed handlers.
    /// += subscribes, -= unsubscribes, invoking calls every handler in order.</summary>
    public class NexusEvent : NexusValue
    {
        public string Name { get; set; }
        public string DelegateType { get; set; }
        public List<NexusValue> Handlers { get; } = new List<NexusValue>();

        public NexusEvent(string name = "", string delegateType = "")
        { Name = name; DelegateType = delegateType; }

        public override string TypeName => "event";
        public override string ToString() => $"<event {Name} ({Handlers.Count} handler(s))>";

        public void AddHandler(NexusValue handler)
        {
            // NexusString handlers are function NAMES (bytecode VM representation)
            if (handler is NexusFunction || handler is NexusInteropFunction ||
                handler is NexusEvent || handler is NexusString)
                Handlers.Add(handler);
        }

        public void RemoveHandler(NexusValue handler)
        {
            for (int i = Handlers.Count - 1; i >= 0; i--)
            {
                bool match = ReferenceEquals(Handlers[i], handler);
                if (!match && Handlers[i] is NexusFunction f1 && handler is NexusFunction f2)
                    match = f1.Name == f2.Name;
                if (!match && handler is NexusString nameStr)
                    match = (Handlers[i] as NexusFunction)?.Name == nameStr.Value ||
                            (Handlers[i] as NexusString)?.Value == nameStr.Value;
                if (match)
                {
                    Handlers.RemoveAt(i);
                    return; // remove one occurrence, like C# delegate removal
                }
            }
        }
    }

    /// <summary>A named delegate type: `delegate int Operation(int a, int b);`.
    /// Mostly nominal — function values remain NexusFunction — but the type name
    /// exists so variables can be declared with it and checked on assignment.</summary>
    public class NexusDelegateType : NexusValue
    {
        public string Name { get; set; }
        public string ReturnType { get; set; }
        public List<Parameter> Parameters { get; } = new List<Parameter>();

        public NexusDelegateType(string name) { Name = name; }

        public override string TypeName => "delegate";
        public override string ToString() =>
            $"<delegate {Name}({string.Join(", ", Parameters.Select(p => p.Type ?? "dynamic"))}) -> {ReturnType ?? "void"}>";
    }

    public class NexusInstance : NexusValue
    {
        public NexusClass Class { get; set; }
        public Dictionary<string, NexusValue> Fields { get; } = new Dictionary<string, NexusValue>();

        public NexusInstance(NexusClass cls)
        {
            Class = cls;
        }

        public override string TypeName => Class != null ? Class.Name : "object";
        public override string ToString() => $"<{TypeName} instance>";

        public NexusValue GetField(string name)
        {
            if (Fields.TryGetValue(name, out var value))
                return value;

            if (Class != null && Class.FindMethod(name) is NexusFunction func)
            {
                // bind the method to this instance
                return new NexusFunction(func.Name)
                {
                    Parameters = func.Parameters,
                    Body = func.Body,
                    Closure = func.Closure,
                    ReturnType = func.ReturnType,
                    BoundThis = this
                };
            }

            return NexusNull.Instance;
        }

        public void SetField(string name, NexusValue value)
        {
            Fields[name] = value;
        }
    }

    public class NexusEnum : NexusValue
    {
        public string EnumName { get; set; }
        public string MemberName { get; set; }
        public long Value { get; set; }

        public NexusEnum(string enumName, string memberName, long value)
        {
            EnumName = enumName; MemberName = memberName; Value = value;
        }

        public override string TypeName => $"enum::{EnumName}";
        public override string ToString() => $"{EnumName}.{MemberName}";
    }

    public static class ValueHelpers
    {
        public static NexusValue FromObject(object obj)
        {
            switch (obj)
            {
                case null: return NexusNull.Instance;
                case NexusValue nv: return nv;
                case long l: return new NexusInt(l);
                case int i: return new NexusInt(i);
                case double d: return new NexusFloat(d);
                case float f: return new NexusFloat(f);
                case string s: return new NexusString(s);
                case bool b: return new NexusBool(b);
                case char c: return new NexusChar(c);
                case List<NexusValue> list: return new NexusList(list);
                default: return new NexusString(obj.ToString());
            }
        }

        public static object ToObject(NexusValue value)
        {
            switch (value)
            {
                case NexusInt ni: return ni.Value;
                case NexusFloat nf: return nf.Value;
                case NexusString ns: return ns.Value;
                case NexusBool nb: return nb.Value;
                case NexusChar nc: return nc.Value;
                case NexusList nl: return nl.Items;
                default: return value;
            }
        }

        public static double ToNumber(NexusValue value)
        {
            switch (value)
            {
                case NexusInt ni: return ni.Value;
                case NexusFloat nf: return nf.Value;
                case NexusBool nb: return nb.Value ? 1 : 0;
                case NexusChar nc: return nc.Value;
                case NexusEnum ne: return ne.Value;
                default: return 0;
            }
        }

        public static long ToInteger(NexusValue value)
        {
            switch (value)
            {
                case NexusInt ni: return ni.Value;
                case NexusFloat nf: return (long)nf.Value;
                case NexusBool nb: return nb.Value ? 1 : 0;
                case NexusChar nc: return nc.Value;
                case NexusEnum ne: return ne.Value;
                default: return 0;
            }
        }

        public static bool IsNumber(NexusValue value) =>
            value is NexusInt || value is NexusFloat || value is NexusBool || value is NexusEnum;

        /// <summary>Default value for a typed declaration without initializer:
        /// `int x;` → 0, `bool b;` → false, `string s;` → "".</summary>
        public static NexusValue DefaultForType(string type)
        {
            switch (type)
            {
                case "int": case "short": case "long": case "byte":
                case "uint": case "ulong":
                    return new NexusInt(0);
                case "float": case "double":
                    return new NexusFloat(0);
                case "bool": case "boolean":
                    return new NexusBool(false);
                case "string":
                    return new NexusString("");
                case "char":
                    return new NexusChar('\0');
                default:
                    return NexusNull.Instance;
            }
        }

        /// <summary>True when the value can be stored in / invoked as a delegate.</summary>
        public static bool IsCallable(NexusValue value) =>
            value is NexusFunction || value is NexusInteropFunction;
    }
}

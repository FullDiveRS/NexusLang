using System;
using System.Collections.Generic;

namespace Nexus.Runtime
{
    public class RuntimeEnvironment
    {
        private readonly Dictionary<string, NexusValue> _variables = new Dictionary<string, NexusValue>();
        private readonly Dictionary<string, NexusValue> _constants = new Dictionary<string, NexusValue>();
        private readonly RuntimeEnvironment _enclosing;

        public RuntimeEnvironment Enclosing => _enclosing;
        public string ScopeName { get; set; }

        public RuntimeEnvironment(RuntimeEnvironment enclosing = null, string scopeName = "local")
        {
            _enclosing = enclosing;
            ScopeName = enclosing == null ? "global" : scopeName;
        }

        public void Define(string name, NexusValue value, bool isConst = false)
        {
            if (isConst)
                _constants[name] = value;
            else
                _variables[name] = value;
        }

        public NexusValue Get(string name)
        {
            if (_variables.TryGetValue(name, out var value))
                return value;
            if (_constants.TryGetValue(name, out var constValue))
                return constValue;

            if (_enclosing != null)
                return _enclosing.Get(name);

            throw new NexusRuntimeException($"Undefined variable '{name}'.");
        }

        public bool TryGet(string name, out NexusValue value)
        {
            if (_variables.TryGetValue(name, out value))
                return true;
            if (_constants.TryGetValue(name, out value))
                return true;
            if (_enclosing != null)
                return _enclosing.TryGet(name, out value);
            value = null;
            return false;
        }

        public void Assign(string name, NexusValue value)
        {
            if (_constants.ContainsKey(name))
                throw new NexusRuntimeException($"Cannot assign to constant '{name}'.");

            if (_variables.ContainsKey(name))
            {
                _variables[name] = value;
                return;
            }

            if (_enclosing != null)
            {
                _enclosing.Assign(name, value);
                return;
            }

            throw new NexusRuntimeException($"Undefined variable '{name}'. Cannot assign.");
        }

        public bool IsDefined(string name)
        {
            if (_variables.ContainsKey(name) || _constants.ContainsKey(name))
                return true;
            return _enclosing != null && _enclosing.IsDefined(name);
        }

        public RuntimeEnvironment GetGlobalScope()
        {
            var env = this;
            while (env._enclosing != null)
                env = env._enclosing;
            return env;
        }

        public IEnumerable<string> GetVariableNames()
        {
            var names = new List<string>(_variables.Keys);
            names.AddRange(_constants.Keys);
            return names;
        }
    }

    public class NexusRuntimeException : Exception
    {
        public int Line { get; set; }
        public NexusRuntimeException(string message, int line = 0) : base(message)
        {
            Line = line;
        }
    }

    public class NexusReturnException : Exception
    {
        public NexusValue Value { get; }
        public NexusReturnException(NexusValue value) : base("return")
        {
            Value = value;
        }
    }

    public class NexusBreakException : Exception
    {
        public NexusBreakException() : base("break") { }
    }

    public class NexusContinueException : Exception
    {
        public NexusContinueException() : base("continue") { }
    }
}

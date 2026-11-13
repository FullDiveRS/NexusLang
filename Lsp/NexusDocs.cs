using System;
using System.Collections.Generic;

namespace Nexus.Lsp
{
    /// <summary>Shared language knowledge tables used by the completion and hover
    /// engines: keywords, builtin functions, asm mnemonics/registers, snippets.</summary>
    public static class NexusDocs
    {
        // ===== keyword documentation (hover) =====
        public static readonly Dictionary<string, string> KeywordDocs = new()
        {
            ["var"]    = "Declaration — `var name = value;` mutable variable.",
            ["let"]    = "Declaration — `let name = value;` mutable variable (alias of var).",
            ["const"]  = "Declaration — `const name = value;` read-only binding; reassignment is an error.",
            ["func"]   = "Declaration — `func name(params) { ... }` a function. Inside classes: a method. `init` is the constructor.",
            ["class"]  = "Declaration — `class Name { ... }` defines a class with fields, methods, properties, operators and events. Inheritance: `class B extends A` or `class B : A`.",
            ["struct"] = "Declaration — `struct Name { ... }` field-only record type.",
            ["enum"]   = "Declaration — `enum Name { A, B = 5 }` named constants. Nexus extension: enums may also contain typed static fields and static methods after the constants.",
            ["interface"] = "Reserved for interface declarations.",
            ["if"]     = "Control flow — `if (cond) { ... } else if (...) { ... } else { ... }`.",
            ["else"]   = "Control flow — else branch of `if`.",
            ["while"]  = "Loop — `while (cond) { ... }`.",
            ["for"]    = "Loop — `for (init; cond; step) { ... }`.",
            ["foreach"] = "Loop — `foreach (item in list) { ... }`. Also iterates ranges: `foreach (i in 1..10)`.",
            ["in"]     = "Used by `foreach (x in xs)`.",
            ["do"]     = "Loop — `do { ... } while (cond);` runs the body at least once.",
            ["switch"] = "Control flow — `switch (value) { case x: ... break; default: ... }`. Cases do not fall through.",
            ["case"]   = "A branch of `switch`.",
            ["default"] = "The fallback branch of `switch`.",
            ["break"]  = "Exit the nearest loop or switch case.",
            ["continue"] = "Jump to the next loop iteration.",
            ["return"] = "Return from the current function: `return expr;` or bare `return;`.",
            ["throw"]  = "Raise an error — `throw \"message\";` caught by `catch`.",
            ["try"]    = "Error handling — `try { ... } catch (e) { ... } finally { ... }`. Without catch the error propagates.",
            ["catch"]  = "Error handler of `try`. Optional binding: `catch (e)`.",
            ["finally"] = "Always-run block of `try`.",
            ["new"]    = "Allocate — `new ClassName(args)` invokes the class `init` constructor.",
            ["this"]   = "The current instance inside methods (alias: `self`).",
            ["super"]  = "Base class. `super(args)` calls the base constructor; `super.method()` calls a base method.",
            ["base"]   = "Alias of `super`.",
            ["static"] = "Modifier — member belongs to the type, not the instance.",
            ["public"] = "Access modifier (accepted for C# familiarity; members are public by default).",
            ["private"] = "Access modifier — conventional; Nexus does not enforce visibility.",
            ["protected"] = "Access modifier — conventional.",
            ["internal"] = "Access modifier — conventional.",
            ["virtual"] = "Modifier accepted for C# familiarity (methods are dynamically dispatched anyway).",
            ["override"] = "Modifier accepted for C# familiarity (overriding is implicit).",
            ["abstract"] = "Modifier accepted for C# familiarity.",
            ["sealed"] = "Modifier accepted for C# familiarity.",
            ["async"]  = "Modifier accepted for C# familiarity.",
            ["await"]  = "Modifier accepted for C# familiarity.",
            ["import"] = "Module import — `import path as alias`.",
            ["using"]  = "Resource scoping — `using (expr) { ... }`.",
            ["namespace"] = "Namespace declaration.",
            ["extern"] = "Marks a function implemented externally (with interop attributes).",
            ["ref"]    = "Parameter modifier — pass by reference.",
            ["out"]    = "Parameter modifier — output parameter.",
            ["params"] = "Parameter modifier — variadic tail parameter.",
            ["get"]    = "Property accessor — `var Name { get { ... } }` or auto `get;`. Runs on read.",
            ["set"]    = "Property accessor — `var Name { set { ... } }` or auto `set;`. Runs on write; the new value is `value`.",
            ["operator"] = "Operator overload — `operator +(other) { ... }` inside a class. Supports + - * / % == != < > <= >=.",
            ["delegate"] = "Function type — `delegate ret Name(params);` declares it, `delegate (params) { ... }` creates an anonymous function.",
            ["event"]  = "Event member — `event DelegateType Name;`. Add/remove handlers with `+=` / `-=`, invoke by calling it: `Name(args)`.",
            ["asm"]    = "Inline assembly — `asm { ... }` (statements) or `var m = asm { label: ... }` (module; labels become callable members).",
            ["print"]  = "Statement — `print expr1, expr2;` writes without a trailing newline.",
            ["println"] = "Statement — `println expr1, expr2;` writes with a trailing newline.",
            ["true"]   = "Boolean literal — true.",
            ["false"]  = "Boolean literal — false.",
            ["null"]   = "Absence value (alias: nil). Pair with `?.` and `??`.",
            ["nil"]    = "Absence value (alias: null). Pair with `?.` and `??`.",
        };

        // ===== builtin function documentation =====
        public static readonly Dictionary<string, (string sig, string desc)> Builtins = new()
        {
            ["print"]        = ("print(value, ...)", "Write values separated by spaces, no newline."),
            ["println"]      = ("println(value, ...)", "Write values separated by spaces plus a newline."),
            ["printf"]       = ("printf(format, ...)", "C-style formatted print: %d %i %u %s %f %x %X %c %%."),
            ["abs"]          = ("abs(n)", "Absolute value."),
            ["sqrt"]         = ("sqrt(n)", "Square root (float)."),
            ["pow"]          = ("pow(base, exp)", "Base raised to exponent."),
            ["floor"]        = ("floor(n)", "Round down to nearest integer."),
            ["ceil"]         = ("ceil(n)", "Round up to nearest integer."),
            ["range"]        = ("range(start, end)", "Inclusive integer list; `1..10` is sugar for range(1, 10)."),
            ["random"]       = ("random()", "Random float in [0, 1)."),
            ["len"]          = ("len(x)", "Length of a string, list or dict."),
            ["typeof"]       = ("typeof(value)", "Type name of a value: int, float, string, bool, list, dict, func, class, instance, null ..."),
            ["str"]          = ("str(value)", "Convert any value to its string form."),
            ["int"]          = ("int(value)", "Convert to integer (truncates floats, parses strings)."),
            ["float"]        = ("float(value)", "Convert to float (parses strings)."),
            ["input"]        = ("input([prompt])", "Read a line from stdin."),
            ["push"]         = ("push(list, value)", "Append value to a list."),
            ["pop"]          = ("pop(list)", "Remove and return the last element."),
            ["time"]         = ("time()", "Unix timestamp in seconds (float)."),
            ["interop_info"] = ("interop_info()", "Report the PINVOKE interop system status."),
        };

        // ===== NASM mnemonics understood by the ASM engine =====
        public static readonly Dictionary<string, (string form, string desc)> AsmMnemonics = new()
        {
            ["MOV"]  = ("mov dst, src", "Copy src into dst (register, memory [label], or immediate)."),
            ["ADD"]  = ("add dst, src", "dst += src."),
            ["SUB"]  = ("sub dst, src", "dst -= src."),
            ["MUL"]  = ("mul src", "EAX = EAX * src (unsigned)."),
            ["IMUL"] = ("imul src", "EAX = EAX * src (signed)."),
            ["DIV"]  = ("div src", "Unsigned divide EAX by src."),
            ["IDIV"] = ("idiv src", "Signed divide EAX by src."),
            ["INC"]  = ("inc dst", "dst += 1."),
            ["DEC"]  = ("dec dst", "dst -= 1."),
            ["NEG"]  = ("neg dst", "dst = -dst."),
            ["AND"]  = ("and dst, src", "Bitwise AND."),
            ["OR"]   = ("or dst, src", "Bitwise OR."),
            ["XOR"]  = ("xor dst, src", "Bitwise XOR."),
            ["NOT"]  = ("not dst", "Bitwise complement."),
            ["SHL"]  = ("shl dst, n", "Shift left."),
            ["SHR"]  = ("shr dst, n", "Shift right."),
            ["PUSH"] = ("push src", "Push onto the ASM stack."),
            ["POP"]  = ("pop dst", "Pop from the ASM stack."),
            ["JMP"]  = ("jmp label", "Unconditional jump to a label."),
            ["CMP"]  = ("cmp a, b", "Compare — sets flags for conditional jumps."),
            ["JE"]   = ("je label", "Jump if equal (ZF)."),
            ["JNE"]  = ("jne label", "Jump if not equal."),
            ["JZ"]   = ("jz label", "Jump if zero."),
            ["JNZ"]  = ("jnz label", "Jump if not zero."),
            ["JG"]   = ("jg label", "Jump if greater (signed)."),
            ["JGE"]  = ("jge label", "Jump if greater or equal (signed)."),
            ["JL"]   = ("jl label", "Jump if less (signed)."),
            ["JLE"]  = ("jle label", "Jump if less or equal (signed)."),
            ["JA"]   = ("ja label", "Jump if above (unsigned)."),
            ["JAE"]  = ("jae label", "Jump if above or equal (unsigned)."),
            ["JB"]   = ("jb label", "Jump if below (unsigned)."),
            ["JBE"]  = ("jbe label", "Jump if below or equal (unsigned)."),
            ["JC"]   = ("jc label", "Jump if carry."),
            ["JNC"]  = ("jnc label", "Jump if not carry."),
            ["CALL"] = ("call label", "Call a label as a subroutine (returns with ret). Also calls Nexus functions defined in the script."),
            ["RET"]  = ("ret", "Return from the current call; result in EAX."),
            ["INT"]  = ("int 0x80", "Software interrupt (0x80 handled by the engine)."),
            ["NOP"]  = ("nop", "Do nothing."),
            ["LOOP"] = ("loop label", "Decrement ECX and jump while ECX != 0."),
            ["XCHG"] = ("xchg a, b", "Exchange two operands."),
            ["LEA"]  = ("lea dst, [label]", "Load the address of a data label."),
            ["TEST"] = ("test a, b", "Bitwise AND, flags only."),
        };

        public static readonly string[] AsmRegisters =
        {
            "eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp",
            "ax", "bx", "cx", "dx", "al", "ah", "bl", "bh", "cl", "ch", "dl", "dh"
        };

        public static readonly Dictionary<string, string> RegisterDocs = new()
        {
            ["eax"] = "Accumulator — holds function return values (fastcall arg 1).",
            ["ebx"] = "Base — fastcall argument register 3 in the Nexus ASM engine.",
            ["ecx"] = "Counter — fastcall argument register 1; LOOP counter.",
            ["edx"] = "Data — fastcall argument register 2.",
            ["esi"] = "Source index — fastcall argument register 4.",
            ["edi"] = "Destination index.",
            ["ebp"] = "Stack base pointer.",
            ["esp"] = "Stack pointer.",
            ["eip"] = "Instruction pointer.",
        };

        // ===== data directives =====
        public static readonly Dictionary<string, (string form, string desc)> AsmDirectives = new()
        {
            ["db"] = ("name db value", "Define byte(s) — also a string: `msg db \"hi\", 0`."),
            ["dw"] = ("name dw value", "Define 16-bit word(s)."),
            ["dd"] = ("name dd value", "Define 32-bit doubleword(s)."),
            ["dq"] = ("name dq value", "Define 64-bit quadword(s)."),
            ["resb"] = ("name resb n", "Reserve n bytes."),
            ["resw"] = ("name resw n", "Reserve n 16-bit words."),
            ["resd"] = ("name resd n", "Reserve n 32-bit doublewords."),
            ["section"] = ("section .data", "Section marker (informational in the engine)."),
            ["global"] = ("global label", "Declare an entry point (informational in the engine)."),
        };

        // ===== structured snippets =====
        public static readonly (string label, string snippet, string detail)[] Snippets =
        {
            ("func",   "func ${1:name}(${2:args}) {\n\t${3}\n}", "Function with body"),
            ("class",  "class ${1:Name} {\n\tvar ${2:field} = ${3:0};\n\n\tfunc init(${4:args}) {\n\t\t${5}\n\t}\n}", "Class with constructor"),
            ("enum",   "enum ${1:Name} {\n\t${2:A}, ${3:B}, ${4:C};\n\n\tpublic static void ${5:Method}(${6}) {\n\t\t${7}\n\t}\n}", "Enum with constants + static members"),
            ("prop",   "var ${1:Name} {\n\tget { return ${2:_value}; }\n\tset { ${2:_value} = value; }\n}", "Property with get/set accessors"),
            ("operator", "operator ${1:+}(${2:other}) {\n\treturn ${3:expr};\n}", "Operator overload"),
            ("delegate", "delegate ${1:void} ${2:Name}(${3});", "Delegate type declaration"),
            ("event",  "event ${1:Action} ${2:Name};", "Event member"),
            ("asmmod", "var ${1:name} = asm {\n\t${2:main}:\n\t\tmov eax, ${3:0}\n\t\tret\n}", "ASM module with a label function"),
            ("ifelse", "if (${1:cond}) {\n\t${2}\n} else {\n\t${3}\n}", "if / else block"),
            ("for",    "for (var ${1:i} = ${2:0}; ${1:i} < ${3:10}; ${1:i}++) {\n\t${4}\n}", "for loop"),
            ("foreach", "foreach (${1:item} in ${2:items}) {\n\t${3}\n}", "foreach loop"),
            ("while",  "while (${1:cond}) {\n\t${2}\n}", "while loop"),
            ("switch", "switch (${1:value}) {\n\tcase ${2:a}:\n\t\t${3}\n\t\tbreak;\n\tdefault:\n\t\t${4}\n\t\tbreak;\n}", "switch with cases"),
            ("try",    "try {\n\t${1}\n} catch (${2:e}) {\n\tprintln(${2:e});\n}", "try / catch"),
            ("asm",    "asm {\n\t${1:main}:\n\t\t${2:mov eax, 0}\n\t\t${3:ret}\n}", "Inline asm block"),
            ("pinvoke", "[DllImport(\"${1:library}\")]\nextern ${2:int} ${3:Name}(${4:args});", "External interop declaration"),
        };

        // ===== primitive / built-in type keywords =====
        public static readonly string[] TypeKeywords =
        {
            "var", "let", "const", "int", "float", "double", "string", "char",
            "bool", "boolean", "void", "byte", "short", "long", "uint", "ulong",
            "object", "dynamic", "list", "dict", "array"
        };
    }
}

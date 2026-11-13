## Note: This Project was made using GLM 5.3-flash and while yes, I did use AI. I also cleaned up and helped fix a few bugs but most of the work was from GLM 5.3-flash. this is 1.0 Expect bugs as this language might be studied or used by Xeo Project to make an SDK.

# Nexus Language v1.2.0

A C#-like programming language **written in pure C#**, with NASM-style inline
assembly, a 10-language interop layer, and three execution backends:
tree-walking **interpreter**, stack-based **bytecode VM**, and an **AOT
transpiler** that emits compilable C#.

```
Nexus source (.nx)
        |
     [Lexer]  <- stall-guarded: can never loop forever
        |
     [Parser] -> AST
        |
        +--> [Interpreter]        full language (recommended)
        +--> [BytecodeCompiler] -> .nbc package -> [VM]
        +--> [AotCompiler]      -> C# source you can compile natively
```

## Quick start

```bash
dotnet build
dotnet run                        # REPL
dotnet run -- run examples/hello.nx
dotnet run -- doctor examples/hello.nx    # diagnose any pipeline stage
dotnet run -- exec examples/functions.nx  # compile + run via the bytecode VM
dotnet run -- compile examples/functions.nx out   # save out.nbc + disassembly
dotnet run -- exec out.nbc                # run the saved bytecode package
dotnet run -- aot examples/hello.nx out.cs # emit native-compilable C#
dotnet run -- bytecode examples/hello.nx   # disassembly
```

`exec` accepts either a `.nx` source file (compile-and-run) or a saved
`.nbc` package (multi-chunk container: magic `NEXUS_BC2`).

REPL commands: `:help :vars :funcs :regs :asm :bytecode :aot :tokens :ast
:load :save :time :interop :reset :exit`

## Language server + VSCode extension (new in 1.2.0)

Nexus now ships its own **Language Server Protocol** implementation — pure C#,
zero dependencies, speaking JSON-RPC over stdio:

```bash
dotnet run -- lsp            # run the language server (used by the extension)
dotnet run -- lsp --log=/tmp/nexus-lsp.log   # with logging
```

The VSCode extension lives in `../vscode-nexus/`. It connects **locally** to
`nexus lsp` and provides:

- **IntelliSense completions** — keywords, snippets, builtins with signatures,
  your own functions/classes/enums/delegates, and member completion after `.`:
  asm module labels, enum constants + statics, class statics/instance members
- **Context-aware NASM completions** inside `asm { }` — mnemonics with docs,
  registers, data directives, and your Nexus variables as writable targets
- **Live diagnostics** from the real Nexus lexer + parser (error recovery
  included, so you see every error, not just the first)
- **Hover documentation** for keywords, builtins and your symbols
- **Go-to-definition** across workspace `.nx` files
- **Document outline** (nested symbols) + workspace symbol search (Ctrl+T)
- Syntax highlighting with an embedded NASM grammar, 22 snippets, and an
  offline fallback mode (static completions + brace checks) when no server
  binary is found

See `../vscode-nexus/README.md` for install + connection instructions.

## Language highlights

### Classes, properties, operator overloads

```csharp
class Vector
{
    var x = 0;
    var y = 0;

    func init(int px, int py) { x = px; y = py; }   // implicit this — no `this.` needed

    operator +(other)  { return Vector(x + other.x, y + other.y); }
    operator *(scalar) { return Vector(x * scalar, y * scalar); }
    operator ==(other) { return x == other.x && y == other.y; }
    operator <(other)  { return x * x + y * y < other.x * other.x + other.y * other.y; }

    var Length {                       // computed property
        get { return sqrt(x * x + y * y); }
    }
    var Tag {
        get { return _tag; }
        set { _tag = "tag:" + value; } // `value` is the implicit setter param
    }
    var Name { get; set; }             // auto-implemented
}

var a = Vector(1, 2);                  // constructor-call style (new optional)
var c = a + b;                         // dispatches to operator +
a.Tag = "left";                        // dispatches to the setter
```

Operator rules (like C# method form): `operator +(other)` — one parameter —
is binary with `this` = left operand; `operator -()` — no parameters — is a
prefix unary. A C#-style two-parameter `operator +(a, b)` also works.

### Enums with members

Plain C#-style enums *and* enums that carry real static members:

```csharp
enum Color { Red = 1, Green = 2, Blue = 4 }

enum Movement
{
    public static bool IsMoving;
    public static string NameOfMovement;
    public static int num;

    public static void MoveForward(int steps, float speed)
    {
        NameOfMovement = "Forward";
        IsMoving = true;
        num = num + steps;
    }
    public static void Stop() { IsMoving = false; }
}

println(str(Color.Red));      // "Color.Red"
Movement.MoveForward(3, 2.5); // enum methods just work
Movement.num = 0;             // enum fields are mutable
switch (mask) { case Color.Green: ... }
```

### Delegates and events

```csharp
delegate int Operation(int a, int b);
delegate void Alert(string msg);

var add = (int x, int y) => { return x + y; };   // lambda assigned to a var
func Apply(Operation op, int a, int b) { return op(a, b); }
Apply(add, 10, 5);

event Alert OnAlert;                              // multicast event

OnAlert += (string msg) => { println("[handler] " + msg); };
OnAlert += HandleAlert;                           // += subscribes
OnAlert("system going down");                     // invocation fires all
OnAlert -= HandleAlert;                           // -= unsubscribes
```

### More C# conveniences

```csharp
var p, q = 2, r;                     // multi-declarators
println($"Hello {name}, 2+2={2 + 2}, braces: {{escaped}}");   // interpolation
var grade = score >= 90 ? "A" : (score >= 80 ? "B" : "C");    // ternary
foreach (var i in 1..5) { ... }      // inclusive range + foreach (lists, strings, dict keys too)
```

### print statements

```csharp
print "A"; println "B", 1 + 2; println("classic form");
```

### statement-level NASM blocks: registers, labels, jumps, data, flags

```csharp
var total = 0;
asm {
    mov ecx, 5
    mov eax, 0
sum_loop:
    add eax, ecx
    dec ecx
    cmp ecx, 0
    jg sum_loop
    mov total, eax
}
```

### asm MODULES: labels become callable functions, data becomes properties

```csharp
var math = asm {
    math:  mov eax, ecx
           add eax, edx
           ret
}
println(math.math(3, 4));        // 7  (fastcall: ecx, edx, ebx, esi -> eax)

var counter = asm {
    db count = 0
    bump:   mov eax, [count]
            inc eax
            mov [count], eax
            ret
}
counter.count = 41;              // Nexus writes into asm memory
counter.bump();                  // 42
```

## Interop

```csharp
#[DllImport("libc.so")]        extern int  c_add(a: int, b: int);
#[NasmImport("math_ops.obj")]  extern int  add(a: int, b: int);
#[JavaImport("java.lang.Math")] extern double jsqrt(x: double);
#[GoImport("libgo.so")]        extern int  Add(a: int, b: int);
#[CSharpImport("Lib.dll")]     extern int  Compute(x: int);
#[VbImport("MyLib.dll")]       extern string GetMessage();
#[FSharpImport("FS.dll")]      extern int  fib(n: int);
#[PythonImport("math")]        extern double py_pi();
#[RustImport("librust.so")]    extern int  fibonacci(n: int);
#[RubyImport("helper.rb")]     extern string greet(name: string);
```

C/C++/Go/Rust calls run through the platform's real ABI when the library is
present; without one, the interop layer reports the call and returns a
simulated result (NASM/Rust have built-in op simulations: add, sub, mul,
fibonacci, ...).

## Backend support matrix

Every feature below was verified by running the examples in `examples/`
through all backends and diffing outputs byte-for-byte.

| Feature                  | Interpreter | Bytecode VM | AOT |
|--------------------------|:-----------:|:-----------:|:---:|
| functions/recursion      | Y           | Y           | Y   |
| default parameters       | Y           | Y           | Y   |
| loops, if/switch/foreach | Y           | Y           | Y   |
| try/catch/throw          | Y           | Y           | Y   |
| ranges `1..5`            | Y           | Y           | Y   |
| string interpolation     | Y           | Y           | Y   |
| multi-declarators        | Y           | Y           | Y   |
| classes/inheritance      | Y           | Y           | Y   |
| enums with members       | Y           | Y           | Y   |
| properties (get/set)     | Y           | Y           | Y   |
| operator overloads       | Y           | Y           | Y   |
| delegates + lambdas      | Y           | Y (value lambdas) | Y |
| events (+=, -=, invoke)  | Y           | Y           | Y   |
| implicit `this` fields   | Y           | Y           | Y (native) |
| asm statement blocks     | Y           | data/arith only (loud for jumps) | line-by-line |
| asm modules              | Y           | - (loud)    | label methods |
| interop                  | Y           | Y           | DllImport |

Anything unsupported produces a **visible error or warning** — nothing is
dropped silently. When in doubt: `nexus doctor <file>` (expected output:
`[OK] Pipeline healthy.`).

## Notes

- NASM mnemonics (mov/add/test/...) are NOT reserved words — you can name
  functions `add` or `test`. Registers (eax...), directives (db/dw/dd) and
  `get`/`set`/`operator`/`delegate`/`event` are reserved keywords.
- `base`/`super` are reserved for constructor chaining (AOT chains them as
  C# `: base(...)` initializers).
- Division is always real-valued (`7 / 2` = `3.5`) in every backend, and a
  zero denominator raises `Division by zero.` (catchable) everywhere.
- `.nbc` files are multi-chunk packages: constants (including enum tables,
  class metadata and interop functions), call-convention slots, and
  instructions survive a save/load round trip; the disassembly
  (`*.nasm.txt`) is human-readable.
- The lexer has a stall guard and the VM an instruction budget, so neither
  a lexer bug nor an infinite Nexus loop can hang the host.

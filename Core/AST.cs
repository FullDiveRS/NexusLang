using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nexus.Core
{
    // ============ BASE NODES ============
    public abstract class AstNode
    {
        public int Line { get; set; }
        public int Column { get; set; }
        public abstract override string ToString();
    }

    public abstract class Expression : AstNode { }
    public abstract class Statement : AstNode { }

    // ============ EXPRESSIONS ============
    public class IntLiteralExpr : Expression
    {
        public long Value { get; }
        public IntLiteralExpr(long value) { Value = value; }
        public override string ToString() => Value.ToString();
    }

    public class FloatLiteralExpr : Expression
    {
        public double Value { get; }
        public FloatLiteralExpr(double value) { Value = value; }
        public override string ToString() => Value.ToString();
    }

    public class StringLiteralExpr : Expression
    {
        public string Value { get; }
        public StringLiteralExpr(string value) { Value = value ?? ""; }
        public override string ToString() => $"\"{Value}\"";
    }

    public class CharLiteralExpr : Expression
    {
        public char Value { get; }
        public CharLiteralExpr(char value) { Value = value; }
        public override string ToString() => $"'{Value}'";
    }

    public class BoolLiteralExpr : Expression
    {
        public bool Value { get; }
        public BoolLiteralExpr(bool value) { Value = value; }
        public override string ToString() => Value.ToString();
    }

    public class NullLiteralExpr : Expression
    {
        public override string ToString() => "null";
    }

    public class IdentifierExpr : Expression
    {
        public string Name { get; }
        public IdentifierExpr(string name) { Name = name; }
        public override string ToString() => Name;
    }

    public class BinaryExpr : Expression
    {
        public Expression Left { get; }
        public string Operator { get; }
        public Expression Right { get; }
        public BinaryExpr(Expression left, string op, Expression right)
        { Left = left; Operator = op; Right = right; }
        public override string ToString() => $"({Left} {Operator} {Right})";
    }

    public class UnaryExpr : Expression
    {
        public string Operator { get; }
        public Expression Operand { get; }
        public bool IsPostfix { get; }
        public UnaryExpr(string op, Expression operand, bool postfix = false)
        { Operator = op; Operand = operand; IsPostfix = postfix; }
        public override string ToString() => IsPostfix ? $"{Operand}{Operator}" : $"{Operator}{Operand}";
    }

    public class AssignExpr : Expression
    {
        public Expression Target { get; }
        public string Operator { get; }
        public Expression Value { get; }
        public AssignExpr(Expression target, string op, Expression value)
        { Target = target; Operator = op; Value = value; }
        public override string ToString() => $"{Target} {Operator} {Value}";
    }

    public class CallExpr : Expression
    {
        public Expression Callee { get; }
        public List<Expression> Arguments { get; } = new();
        public CallExpr(Expression callee, List<Expression> args)
        { Callee = callee; Arguments = args; }
        public override string ToString() => $"{Callee}({string.Join(", ", Arguments)})";
    }

    public class MemberAccessExpr : Expression
    {
        public Expression Object { get; }
        public string Member { get; }

        /// <summary>true for the null-conditional form `obj?.member` — evaluating
        /// the member on null yields null instead of an error.</summary>
        public bool NullConditional { get; set; }

        public MemberAccessExpr(Expression obj, string member)
        { Object = obj; Member = member; }
        public override string ToString() =>
            NullConditional ? $"{Object}?.{Member}" : $"{Object}.{Member}";
    }

    public class IndexExpr : Expression
    {
        public Expression Object { get; }
        public Expression Index { get; }
        public IndexExpr(Expression obj, Expression index)
        { Object = obj; Index = index; }
        public override string ToString() => $"{Object}[{Index}]";
    }

    public class TernaryExpr : Expression
    {
        public Expression Condition { get; }
        public Expression ThenExpr { get; }
        public Expression ElseExpr { get; }
        public TernaryExpr(Expression cond, Expression then, Expression elseE)
        { Condition = cond; ThenExpr = then; ElseExpr = elseE; }
        public override string ToString() => $"{Condition} ? {ThenExpr} : {ElseExpr}";
    }

    public class NewExpr : Expression
    {
        public string TypeName { get; }
        public List<Expression> Arguments { get; } = new();
        public NewExpr(string typeName, List<Expression> args)
        { TypeName = typeName; Arguments = args; }
        public override string ToString() => $"new {TypeName}({string.Join(", ", Arguments)})";
    }

    public class ListExpr : Expression
    {
        public List<Expression> Elements { get; } = new();
        public ListExpr(List<Expression> elements) { Elements = elements; }
        public override string ToString() => $"[{string.Join(", ", Elements)}]";
    }

    public class DictExpr : Expression
    {
        public Dictionary<Expression, Expression> Pairs { get; } = new();
        public DictExpr(Dictionary<Expression, Expression> pairs) { Pairs = pairs; }
        public override string ToString()
        {
            var sb = new StringBuilder("{");
            foreach (var kvp in Pairs)
                sb.Append($" {kvp.Key}: {kvp.Value},");
            sb.Append(" }");
            return sb.ToString();
        }
    }

    public class ThisExpr : Expression
    {
        public override string ToString() => "this";
    }

    /// <summary>super(args) — invokes the base class constructor.</summary>
    public class BaseInitCallExpr : Expression
    {
        public List<Expression> Arguments { get; } = new();
        public BaseInitCallExpr(List<Expression> args) { Arguments = args; }
        public override string ToString() => $"super({string.Join(", ", Arguments)})";
    }

    public class LambdaExpr : Expression
    {
        public List<Parameter> Parameters { get; } = new();
        public List<Statement> Body { get; } = new();
        public bool IsExpressionBody { get; set; }
        public Expression ExpressionBody { get; set; }
        public override string ToString() => $"({string.Join(", ", Parameters)}) => ...";
    }

    public class AsExpr : Expression
    {
        public Expression Expression { get; }
        public string TargetType { get; }
        public AsExpr(Expression expr, string type) { Expression = expr; TargetType = type; }
        public override string ToString() => $"{Expression} as {TargetType}";
    }

    public class IsExpr : Expression
    {
        public Expression Expression { get; }
        public string TargetType { get; }
        public IsExpr(Expression expr, string type) { Expression = expr; TargetType = type; }
        public override string ToString() => $"{Expression} is {TargetType}";
    }

    /// <summary>*ptr — NASM-style memory dereference via the ASM engine.</summary>
    public class PointerExpr : Expression
    {
        public Expression Address { get; }
        public PointerExpr(Expression addr) { Address = addr; }
        public override string ToString() => $"*({Address})";
    }

    /// <summary>&amp;var — address of a variable (ASM engine memory slot).</summary>
    public class AddressOfExpr : Expression
    {
        public Expression Target { get; }
        public AddressOfExpr(Expression target) { Target = target; }
        public override string ToString() => $"&({Target})";
    }

    /// <summary>asm block in expression position: var m = asm { label: ... }</summary>
    public class AsmModuleExpr : Expression
    {
        public AsmBlock Block { get; }
        public AsmModuleExpr(AsmBlock block) { Block = block; }
        public override string ToString() =>
            $"asm module ({Block.Instructions.Count} instructions, {Block.Labels.Count} labels)";
    }

    /// <summary>1..5 — inclusive integer range; evaluates to a list of ints.
    /// Usable directly in foreach and anywhere a list is expected.</summary>
    public class RangeExpr : Expression
    {
        public Expression Start { get; }
        public Expression End { get; }
        public RangeExpr(Expression start, Expression end) { Start = start; End = end; }
        public override string ToString() => $"{Start}..{End}";
    }

    /// <summary>"text {expr} more" — interpolated string. Parts hold either a
    /// const string segment or an Expression to evaluate and stringify.</summary>
    public class InterpStringExpr : Expression
    {
        public List<object> Parts { get; } = new List<object>();
        public override string ToString() =>
            "$\"" + string.Join("", Parts.Select(p => p is Expression e ? $"{{{e}}}" : p)) + "\"";
    }

    // ============ NASM/ASM ============
    public class AsmBlock : Statement
    {
        public List<AsmInstruction> Instructions { get; } = new();
        public List<AsmDataDef> DataDefinitions { get; } = new();
        public List<string> Labels { get; } = new();

        /// <summary>Label name → instruction index it points to.
        /// Required for jmp/jcc/call to resolve targets.</summary>
        public Dictionary<string, int> LabelPositions { get; } = new();

        public bool IsInline { get; set; }

        public AsmBlock(int line = 0) { Line = line; }

        public override string ToString()
        {
            var sb = new StringBuilder("asm {\n");
            foreach (var inst in Instructions)
                sb.AppendLine($"    {inst}");
            sb.Append("}");
            return sb.ToString();
        }
    }

    public class AsmInstruction
    {
        public string Mnemonic { get; }
        public string Operand1 { get; }
        public string Operand2 { get; }
        public int Line { get; }

        public AsmInstruction(string mnemonic, string op1 = null, string op2 = null, int line = 0)
        {
            Mnemonic = mnemonic?.ToUpperInvariant() ?? "NOP";
            Operand1 = op1;
            Operand2 = op2;
            Line = line;
        }

        public override string ToString()
        {
            if (Operand1 == null) return Mnemonic;
            if (Operand2 == null) return $"{Mnemonic} {Operand1}";
            return $"{Mnemonic} {Operand1}, {Operand2}";
        }
    }

    public class AsmDataDef
    {
        public string Name { get; set; }
        public string Directive { get; set; }  // db, dw, dd, dq
        public List<object> Values { get; } = new();
    }

    // ============ INTEROP DECLARATIONS ============
    public class InteropDeclaration : Statement
    {
        public string InteropType { get; set; }
        public string Library { get; set; }
        public string EntryPoint { get; set; }
        public string ReturnType { get; set; }
        public string MethodName { get; set; }
        public List<Parameter> Parameters { get; } = new();
        public Dictionary<string, string> AdditionalOptions { get; } = new();

        public override string ToString() =>
            $"[{InteropType}(\"{Library}\")] extern {ReturnType} {MethodName}(...)";
    }

    // ============ STATEMENTS ============
    public class VariableDecl : Statement
    {
        public string Name { get; set; }
        public string VarType { get; set; }
        public string Modifier { get; set; } // var, let, const
        public Expression Initializer { get; set; }
        public bool IsStatic { get; set; }

        public VariableDecl(string name, string type, string modifier, Expression init)
        { Name = name; VarType = type; Modifier = modifier; Initializer = init; }

        public override string ToString() =>
            $"{Modifier} {Name}: {VarType}" + (Initializer != null ? $" = {Initializer}" : "");
    }

    /// <summary>`var a, b = 2, c;` — several declarators sharing one `var`.
    /// All declarators are declared in the SAME scope.</summary>
    public class MultiVariableDecl : Statement
    {
        public List<VariableDecl> Declarations { get; } = new();
        public override string ToString() => string.Join(", ", Declarations);
    }

    public class PrintStmt : Statement
    {
        /// <summary>Expressions to print. Multiple values are joined with a single space.</summary>
        public List<Expression> Values { get; } = new();

        /// <summary>true = println (append newline), false = print (no newline).</summary>
        public bool Newline { get; set; } = true;

        public PrintStmt() { }

        public PrintStmt(List<Expression> values, bool newline = true)
        { Values.AddRange(values ?? new List<Expression>()); Newline = newline; }

        public override string ToString()
        {
            var kw = Newline ? "println" : "print";
            return Values.Count == 0 ? $"{kw};" : $"{kw} {string.Join(", ", Values)};";
        }
    }

    public class ExpressionStmt : Statement
    {
        public Expression Expression { get; }
        public ExpressionStmt(Expression expr) { Expression = expr; }
        public override string ToString() => Expression.ToString();
    }

    public class BlockStmt : Statement
    {
        public List<Statement> Statements { get; } = new();
        public BlockStmt(List<Statement> stmts) { Statements = stmts ?? new List<Statement>(); }
        public override string ToString() => $"{{ ... {Statements.Count} statements ... }}";
    }

    public class IfStmt : Statement
    {
        public Expression Condition { get; }
        public Statement ThenBranch { get; }
        public Statement ElseBranch { get; }
        public IfStmt(Expression cond, Statement then, Statement elseB)
        { Condition = cond; ThenBranch = then; ElseBranch = elseB; }
        public override string ToString() => $"if ({Condition}) ...";
    }

    public class WhileStmt : Statement
    {
        public Expression Condition { get; }
        public Statement Body { get; }
        public bool IsDoWhile { get; }
        public WhileStmt(Expression cond, Statement body, bool isDoWhile = false)
        { Condition = cond; Body = body; IsDoWhile = isDoWhile; }
        public override string ToString() =>
            IsDoWhile ? "do ... while (...)" : $"while ({Condition}) ...";
    }

    public class ForStmt : Statement
    {
        public Statement Initializer { get; }
        public Expression Condition { get; }
        public Expression Increment { get; }
        public Statement Body { get; }
        public ForStmt(Statement init, Expression cond, Expression inc, Statement body)
        { Initializer = init; Condition = cond; Increment = inc; Body = body; }
        public override string ToString() => "for (...; ...; ...) ...";
    }

    public class ForeachStmt : Statement
    {
        public string VariableName { get; }
        public Expression Iterable { get; }
        public Statement Body { get; }
        public ForeachStmt(string varName, Expression iterable, Statement body)
        { VariableName = varName; Iterable = iterable; Body = body; }
        public override string ToString() => $"foreach ({VariableName} in {Iterable}) ...";
    }

    public class ReturnStmt : Statement
    {
        public Expression Value { get; }
        public ReturnStmt(Expression value) { Value = value; }
        public override string ToString() => Value != null ? $"return {Value}" : "return";
    }

    public class BreakStmt : Statement
    {
        public override string ToString() => "break";
    }

    public class ContinueStmt : Statement
    {
        public override string ToString() => "continue";
    }

    public class FunctionDecl : Statement
    {
        public string Name { get; set; }
        public string ReturnType { get; set; }
        public List<Parameter> Parameters { get; } = new();
        public List<Statement> Body { get; set; } = new();
        public bool IsStatic { get; set; }
        public bool IsExtern { get; set; }
        public string ExternLibrary { get; set; }
        public List<InteropDeclaration> InteropDeclarations { get; } = new();
        public bool IsAsync { get; set; }

        /// <summary>Operator overload: `operator +(a, b)` → IsOperator = true,
        /// OperatorSymbol = "+". Arity (1 = unary, 2 = binary) comes from
        /// Parameters.Count.</summary>
        public bool IsOperator { get; set; }
        public string OperatorSymbol { get; set; }

        public override string ToString() =>
            IsOperator
                ? $"{ReturnType ?? "var"} operator {OperatorSymbol}({string.Join(", ", Parameters)})"
                : $"func {Name}({string.Join(", ", Parameters)}) -> {ReturnType ?? "void"}";
    }

    /// <summary>Property member: `var Name { get; set; }`. Auto-implemented when
    /// the accessor has no body (backed by an implicit field of the same name).</summary>
    public class PropertyDecl : Statement
    {
        public string Name { get; set; }
        public string PropertyType { get; set; }
        public bool IsStatic { get; set; }
        public bool HasGetter { get; set; }
        public bool HasSetter { get; set; }

        /// <summary>Accessor bodies. Null accessor body + HasGetter/HasSetter
        /// flag = auto-implemented. Setter's first parameter is `value`.</summary>
        public FunctionDecl GetterBody { get; set; }
        public FunctionDecl SetterBody { get; set; }

        public override string ToString()
        {
            var parts = new List<string>();
            if (HasGetter) parts.Add(GetterBody != null ? "get { ... }" : "get;");
            if (HasSetter) parts.Add(SetterBody != null ? "set { ... }" : "set;");
            return $"var {Name} {{ {string.Join(" ", parts)} }}";
        }
    }

    /// <summary>`delegate int Operation(int a, int b);` — a named function type.</summary>
    public class DelegateDecl : Statement
    {
        public string Name { get; set; }
        public string ReturnType { get; set; }
        public List<Parameter> Parameters { get; } = new();

        public override string ToString() =>
            $"delegate {ReturnType ?? "void"} {Name}({string.Join(", ", Parameters)})";
    }

    /// <summary>`event Operation OnTick;` — a multicast event of delegate type.</summary>
    public class EventDecl : Statement
    {
        public string Name { get; set; }
        public string DelegateType { get; set; }
        public bool IsStatic { get; set; }

        public override string ToString() =>
            $"event {DelegateType} {Name}" + (IsStatic ? " (static)" : "");
    }

    public class Parameter
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public Expression DefaultValue { get; set; }
        public bool IsRef { get; set; }
        public bool IsOut { get; set; }
        public bool IsParams { get; set; }

        public Parameter(string name, string type)
        { Name = name; Type = type; }

        public override string ToString() =>
            $"{(IsRef ? "ref " : "")}{(IsOut ? "out " : "")}{(IsParams ? "params " : "")}{Name}: {Type}";
    }

    public class ClassDecl : Statement
    {
        public string Name { get; set; }
        public string BaseClass { get; set; }
        public List<string> Interfaces { get; } = new();
        public List<Statement> Members { get; } = new();
        public bool IsStatic { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public string AccessModifier { get; set; } = "public";

        public override string ToString() =>
            $"class {Name}" + (BaseClass != null ? $" : {BaseClass}" : "");
    }

    public class StructDecl : Statement
    {
        public string Name { get; set; }
        public List<Statement> Fields { get; } = new();
        public override string ToString() => $"struct {Name}";
    }

    public class EnumDecl : Statement
    {
        public string Name { get; set; }
        public List<Tuple<string, long>> Members { get; } = new();

        /// <summary>Static methods / fields / properties declared inside the enum
        /// body after the member list (Nexus extension over C#).</summary>
        public List<Statement> MemberStatements { get; } = new();

        public override string ToString()
        {
            var names = Members.Select(m => m.Item1).ToList();
            var extra = MemberStatements.Count > 0 ? $" + {MemberStatements.Count} members" : "";
            return $"enum {Name} {{ {string.Join(", ", names)} }}{extra}";
        }
    }

    public class NamespaceDecl : Statement
    {
        public string Name { get; set; }
        public List<Statement> Body { get; } = new();
        public NamespaceDecl(string name, List<Statement> body)
        { Name = name; Body = body ?? new List<Statement>(); }
        public override string ToString() => $"namespace {Name}";
    }

    public class ImportStmt : Statement
    {
        public string Module { get; set; }
        public string Alias { get; set; }
        public ImportStmt(string module, string alias = null)
        { Module = module; Alias = alias; }
        public override string ToString() =>
            $"import {Module}" + (Alias != null ? $" as {Alias}" : "");
    }

    public class UsingStmt : Statement
    {
        public string Resource { get; set; }
        public Statement Body { get; set; }
        public UsingStmt(string resource, Statement body)
        { Resource = resource; Body = body; }
        public override string ToString() => $"using ({Resource}) {{ ... }}";
    }

    public class TryCatchStmt : Statement
    {
        public Statement TryBlock { get; set; }
        public string CatchVariable { get; set; }
        public Statement CatchBlock { get; set; }
        public Statement FinallyBlock { get; set; }

        public TryCatchStmt(Statement tryB, string catchVar, Statement catchB, Statement finallyB)
        { TryBlock = tryB; CatchVariable = catchVar; CatchBlock = catchB; FinallyBlock = finallyB; }

        public override string ToString() => "try { ... } catch { ... }";
    }

    public class ThrowStmt : Statement
    {
        public Expression Value { get; }
        public ThrowStmt(Expression value) { Value = value; }
        public override string ToString() => $"throw {Value}";
    }

    public class SwitchStmt : Statement
    {
        public Expression Subject { get; }
        public List<SwitchCase> Cases { get; } = new();
        public Statement DefaultCase { get; set; }
        public SwitchStmt(Expression subject) { Subject = subject; }
        public override string ToString() => $"switch ({Subject}) {{ ... }}";
    }

    public class SwitchCase
    {
        public Expression Value { get; set; }
        public List<Statement> Body { get; set; } = new();
    }

    public class ProgramNode : AstNode
    {
        public List<Statement> Statements { get; } = new();
        public override string ToString() => $"Program with {Statements.Count} statements";
    }
}

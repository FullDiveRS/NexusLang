using System;
using System.Collections.Generic;

namespace Nexus.Core
{
    public enum TokenType
    {
        // Literals
        IntLiteral, FloatLiteral, StringLiteral, CharLiteral, BoolLiteral, NullLiteral,

        // Identifiers & Keywords
        Identifier,
        Var, Let, Const, Func, Class, Struct, Enum, Interface,
        If, Else, While, For, Foreach, In, Do, Switch, Case, Default,
        Break, Continue, Return, Throw, Try, Catch, Finally,
        New, This, Base, Static, Public, Private, Protected, Internal,
        Virtual, Override, Abstract, Sealed, Async, Await,
        Import, Using, Namespace, Extern, Unsafe, Ref, Out, Params,
        Get, Set, Operator, Implicit, Explicit, Delegate, Event,

        // NASM keywords (directives + registers; mnemonics are ordinary
        // identifiers so user code can freely use add/test/call as names)
        Asm, Db, Dw, Dd, Dq, Resb, Resw, Resd, Section, Global,
        Eax, Ebx, Ecx, Edx, Esi, Edi, Ebp, Esp,
        Ax, Bx, Cx, Dx, Ah, Al, Bh, Bl, Ch, Cl, Dh, Dl,

        // Types (must stay contiguous: IntType..TaskType — parser range-checks it)
        IntType, FloatType, DoubleType, StringType, CharType, BoolType,
        VoidType, ByteType, ShortType, LongType, UIntType, ULongType,
        ObjectType, DynamicType, PointerType, ArrayType, ListType,
        DictType, TaskType,

        // Operators
        Plus, Minus, Star, Slash, Percent,
        PlusPlus, MinusMinus,
        Assign, PlusAssign, MinusAssign, StarAssign, SlashAssign, PercentAssign,
        Equals, NotEquals, LessThan, GreaterThan, LessEqual, GreaterEqual,
        AndAnd, OrOr, Not, BitAnd, BitOr, BitXor, BitNot,
        ShiftLeft, ShiftRight,
        Arrow, FatArrow, Question, QuestionQuestion, Colon, Semicolon,
        Dot, DotDot, Comma, At, Hash, Dollar, Tilde,

        // Delimiters
        LeftParen, RightParen, LeftBrace, RightBrace, LeftBracket, RightBracket,

        // Special
        DllImport, JavaImport, NasmImport, GoImport, CSharpImport,
        VbImport, FSharpImport, PythonImport, RustImport, RubyImport,
        InteropAttribute,
        Print, Println,
        EOF, Newline, Error, Comment, Directive
    }

    public class Token
    {
        public TokenType Type { get; }
        public string Value { get; }
        public object Literal { get; }
        public int Line { get; }
        public int Column { get; }
        public int Position { get; }

        public Token(TokenType type, string value, int line, int col, int pos)
        {
            Type = type; Value = value; Literal = value;
            Line = line; Column = col; Position = pos;
        }

        public Token(TokenType type, string value, object literal, int line, int col, int pos)
        {
            Type = type; Value = value; Literal = literal;
            Line = line; Column = col; Position = pos;
        }

        public override string ToString() => $"[{Type}:'{Value}'] @{Line}:{Column}";
    }

    public static class Keywords
    {
        // NOTE: NASM mnemonics (mov/add/call/ret/...) are deliberately NOT reserved —
        // the assembler treats identifiers as mnemonics, so user code can define
        // func add(...) etc. Registers stay reserved for operand parsing.
        private static readonly Dictionary<string, TokenType> _keywords = new()
        {
            // Declaration keywords
            ["var"] = TokenType.Var,
            ["let"] = TokenType.Let,
            ["const"] = TokenType.Const,
            ["func"] = TokenType.Func,
            ["fn"] = TokenType.Func,
            ["def"] = TokenType.Func,
            ["class"] = TokenType.Class,
            ["struct"] = TokenType.Struct,
            ["enum"] = TokenType.Enum,
            ["interface"] = TokenType.Interface,

            // Control flow
            ["if"] = TokenType.If,
            ["else"] = TokenType.Else,
            ["while"] = TokenType.While,
            ["for"] = TokenType.For,
            ["foreach"] = TokenType.Foreach,
            ["in"] = TokenType.In,
            ["do"] = TokenType.Do,
            ["switch"] = TokenType.Switch,
            ["case"] = TokenType.Case,
            ["default"] = TokenType.Default,
            ["break"] = TokenType.Break,
            ["continue"] = TokenType.Continue,
            ["return"] = TokenType.Return,
            ["throw"] = TokenType.Throw,
            ["try"] = TokenType.Try,
            ["catch"] = TokenType.Catch,
            ["finally"] = TokenType.Finally,

            // Object-oriented
            ["new"] = TokenType.New,
            ["this"] = TokenType.This,
            ["self"] = TokenType.This,
            ["base"] = TokenType.Base,
            ["super"] = TokenType.Base,
            ["static"] = TokenType.Static,
            ["public"] = TokenType.Public,
            ["private"] = TokenType.Private,
            ["protected"] = TokenType.Protected,
            ["internal"] = TokenType.Internal,
            ["virtual"] = TokenType.Virtual,
            ["override"] = TokenType.Override,
            ["abstract"] = TokenType.Abstract,
            ["sealed"] = TokenType.Sealed,
            ["async"] = TokenType.Async,
            ["await"] = TokenType.Await,

            // Module system
            ["import"] = TokenType.Import,
            ["using"] = TokenType.Using,
            ["namespace"] = TokenType.Namespace,
            ["extern"] = TokenType.Extern,
            ["unsafe"] = TokenType.Unsafe,
            ["ref"] = TokenType.Ref,
            ["out"] = TokenType.Out,
            ["params"] = TokenType.Params,

            // Members: get/set/operator/delegate/event are C#-style keywords.
            // They are contextual in spirit but reserved here so properties,
            // operator overloads, delegate types and events parse unambiguously.
            ["get"] = TokenType.Get,
            ["set"] = TokenType.Set,
            ["operator"] = TokenType.Operator,
            ["delegate"] = TokenType.Delegate,
            ["event"] = TokenType.Event,

            // Assembler directives + data definitions
            ["asm"] = TokenType.Asm,
            ["db"] = TokenType.Db,
            ["dw"] = TokenType.Dw,
            ["dd"] = TokenType.Dd,
            ["dq"] = TokenType.Dq,
            ["resb"] = TokenType.Resb,
            ["resw"] = TokenType.Resw,
            ["resd"] = TokenType.Resd,
            ["section"] = TokenType.Section,
            ["global"] = TokenType.Global,

            // Registers
            ["eax"] = TokenType.Eax, ["ebx"] = TokenType.Ebx,
            ["ecx"] = TokenType.Ecx, ["edx"] = TokenType.Edx,
            ["esi"] = TokenType.Esi, ["edi"] = TokenType.Edi,
            ["ebp"] = TokenType.Ebp, ["esp"] = TokenType.Esp,
            ["ax"] = TokenType.Ax, ["bx"] = TokenType.Bx,
            ["cx"] = TokenType.Cx, ["dx"] = TokenType.Dx,
            ["al"] = TokenType.Al, ["bl"] = TokenType.Bl,
            ["cl"] = TokenType.Cl, ["dl"] = TokenType.Dl,
            ["ah"] = TokenType.Ah, ["bh"] = TokenType.Bh,
            ["ch"] = TokenType.Ch, ["dh"] = TokenType.Dh,

            // Types
            ["int"] = TokenType.IntType,
            ["float"] = TokenType.FloatType,
            ["double"] = TokenType.DoubleType,
            ["string"] = TokenType.StringType,
            ["char"] = TokenType.CharType,
            ["bool"] = TokenType.BoolType,
            ["boolean"] = TokenType.BoolType,
            ["void"] = TokenType.VoidType,
            ["byte"] = TokenType.ByteType,
            ["short"] = TokenType.ShortType,
            ["long"] = TokenType.LongType,
            ["uint"] = TokenType.UIntType,
            ["ulong"] = TokenType.ULongType,
            ["object"] = TokenType.ObjectType,
            ["dynamic"] = TokenType.DynamicType,
            ["list"] = TokenType.ListType,
            ["dict"] = TokenType.DictType,
            ["array"] = TokenType.ArrayType,

            // Print statements
            ["print"] = TokenType.Print,
            ["println"] = TokenType.Println,

            // Literals
            ["true"] = TokenType.BoolLiteral,
            ["false"] = TokenType.BoolLiteral,
            ["null"] = TokenType.NullLiteral,
            ["nil"] = TokenType.NullLiteral,
        };

        public static bool IsKeyword(string text) => _keywords.ContainsKey(text);
        public static TokenType GetKeywordType(string text) =>
            _keywords.TryGetValue(text, out var type) ? type : TokenType.Identifier;
    }
}

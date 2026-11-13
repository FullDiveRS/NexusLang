using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Core
{
    /// <summary>Recursive-descent parser producing the Nexus AST.</summary>
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _position;
        private readonly List<string> _errors = new();

        public List<string> Errors => _errors;

        public Parser(List<Token> tokens)
        {
            _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        }

        private Token Current => Peek(0);
        private Token Previous => _position > 0 ? _tokens[_position - 1] : _tokens[0];

        private Token Peek(int offset = 0)
        {
            int idx = _position + offset;
            return idx >= _tokens.Count ? _tokens[_tokens.Count - 1] : _tokens[idx];
        }

        private Token Advance()
        {
            if (!IsAtEnd()) _position++;
            return Previous;
        }

        private bool IsAtEnd() => Peek().Type == TokenType.EOF;
        private bool Check(TokenType type) => !IsAtEnd() && Current.Type == type;

        private bool Match(params TokenType[] types)
        {
            foreach (var type in types)
            {
                if (Check(type)) { Advance(); return true; }
            }
            return false;
        }

        private Token Consume(TokenType type, string message)
        {
            if (Check(type)) return Advance();
            throw new ParseException(
                $"Line {Current.Line}: {message}. Got '{Current.Value}' ({Current.Type})");
        }

        private void SkipNewlines()
        {
            while (Check(TokenType.Newline) || Check(TokenType.Semicolon))
                Advance();
        }

        private bool IsStatementTerminator()
        {
            return Check(TokenType.Newline) || Check(TokenType.Semicolon) ||
                   Check(TokenType.RightBrace) || IsAtEnd();
        }

        // ============ PROGRAM ============
        public ProgramNode ParseProgram()
        {
            var program = new ProgramNode();
            SkipNewlines();

            while (!IsAtEnd())
            {
                try
                {
                    var stmt = ParseDeclaration();
                    if (stmt != null)
                        program.Statements.Add(stmt);
                }
                catch (ParseException ex)
                {
                    _errors.Add(ex.Message);
                    Synchronize();
                }
                SkipNewlines();
            }

            return program;
        }

        private void Synchronize()
        {
            Advance();
            while (!IsAtEnd())
            {
                if (Previous.Type == TokenType.Semicolon || Previous.Type == TokenType.Newline)
                    return;

                switch (Current.Type)
                {
                    case TokenType.Class:
                    case TokenType.Func:
                    case TokenType.Var:
                    case TokenType.Let:
                    case TokenType.Const:
                    case TokenType.If:
                    case TokenType.While:
                    case TokenType.For:
                    case TokenType.Return:
                    case TokenType.Namespace:
                    case TokenType.Import:
                    case TokenType.Print:
                    case TokenType.Println:
                    case TokenType.Enum:
                    case TokenType.Delegate:
                    case TokenType.Event:
                        return;
                }
                Advance();
            }
        }

        // ============ DECLARATIONS ============
        private Statement ParseDeclaration()
        {
            SkipNewlines();

            if (Match(TokenType.Namespace)) return ParseNamespace();
            if (Match(TokenType.Import)) return ParseImport();
            if (Match(TokenType.Class)) return ParseClass();
            if (Match(TokenType.Struct)) return ParseStruct();
            if (Match(TokenType.Enum)) return ParseEnum();
            if (Match(TokenType.Func)) return ParseFunction();
            if (Match(TokenType.Delegate)) return ParseDelegateDeclaration();
            if (Match(TokenType.Event)) return ParseEventDeclaration();

            if (IsInteropAttribute())
                return ParseInteropDeclaration();

            if (Match(TokenType.Extern))
                return ParseExternDeclaration();

            if (Check(TokenType.Public) || Check(TokenType.Private) ||
                Check(TokenType.Protected) || Check(TokenType.Internal) ||
                Check(TokenType.Static) || Check(TokenType.Async) ||
                Check(TokenType.Virtual) || Check(TokenType.Override) ||
                Check(TokenType.Abstract))
            {
                return ParseModifiedDeclaration();
            }

            return ParseStatement();
        }

        private bool IsInteropAttribute()
        {
            return Current.Type == TokenType.DllImport ||
                   Current.Type == TokenType.JavaImport ||
                   Current.Type == TokenType.NasmImport ||
                   Current.Type == TokenType.GoImport ||
                   Current.Type == TokenType.CSharpImport ||
                   Current.Type == TokenType.VbImport ||
                   Current.Type == TokenType.FSharpImport ||
                   Current.Type == TokenType.PythonImport ||
                   Current.Type == TokenType.RustImport ||
                   Current.Type == TokenType.RubyImport;
        }

        private Statement ParseModifiedDeclaration()
        {
            bool isStatic = false;
            bool isAsync = false;

            while (Match(TokenType.Public, TokenType.Private, TokenType.Protected,
                         TokenType.Internal, TokenType.Static, TokenType.Async,
                         TokenType.Virtual, TokenType.Override, TokenType.Abstract))
            {
                switch (Previous.Type)
                {
                    case TokenType.Static: isStatic = true; break;
                    case TokenType.Async: isAsync = true; break;
                }
            }

            if (Match(TokenType.Func))
            {
                var func = ParseFunction();
                if (func is FunctionDecl fd)
                {
                    fd.IsStatic = isStatic;
                    fd.IsAsync = isAsync;
                }
                return func;
            }

            return ParseVariableOrStatement();
        }

        private Statement ParseVariableOrStatement()
        {
            if (Match(TokenType.Var, TokenType.Let, TokenType.Const))
            {
                string modifier = Previous.Type switch
                {
                    TokenType.Var => "var",
                    TokenType.Let => "let",
                    TokenType.Const => "const",
                    _ => "var"
                };

                var nameToken = Consume(TokenType.Identifier, "Expected variable name");
                string type = "dynamic";

                if (Match(TokenType.Colon))
                    type = ParseTypeName();

                Expression initializer = null;
                if (Match(TokenType.Assign))
                    initializer = ParseExpression();

                if (!IsStatementTerminator() && !Check(TokenType.Comma))
                    Consume(TokenType.Semicolon, "Expected ';' after variable declaration");

                // multi-declarator: var a, b = 2, c;
                if (Check(TokenType.Comma))
                {
                    var multi = new MultiVariableDecl { Line = nameToken.Line };
                    multi.Declarations.Add(
                        new VariableDecl(nameToken.Value, type, modifier, initializer)
                        { Line = nameToken.Line });

                    while (Match(TokenType.Comma))
                    {
                        var extraName = Consume(TokenType.Identifier, "Expected variable name");
                        string extraType = "dynamic";
                        if (Match(TokenType.Colon))
                            extraType = ParseTypeName();
                        Expression extraInit = null;
                        if (Match(TokenType.Assign))
                            extraInit = ParseExpression();

                        if (!IsStatementTerminator() && !Check(TokenType.Comma))
                            Consume(TokenType.Semicolon, "Expected ';' after variable declaration");

                        multi.Declarations.Add(
                            new VariableDecl(extraName.Value, extraType, modifier, extraInit)
                            { Line = extraName.Line });
                    }
                    return multi;
                }

                return new VariableDecl(nameToken.Value, type, modifier, initializer)
                { Line = nameToken.Line };
            }

            return ParseStatement();
        }

        private string ParseTypeName()
        {
            var typeToken = Current;
            string typeName;

            switch (typeToken.Type)
            {
                case TokenType.IntType: typeName = "int"; break;
                case TokenType.FloatType: typeName = "float"; break;
                case TokenType.DoubleType: typeName = "double"; break;
                case TokenType.StringType: typeName = "string"; break;
                case TokenType.CharType: typeName = "char"; break;
                case TokenType.BoolType: typeName = "bool"; break;
                case TokenType.VoidType: typeName = "void"; break;
                case TokenType.ByteType: typeName = "byte"; break;
                case TokenType.ShortType: typeName = "short"; break;
                case TokenType.LongType: typeName = "long"; break;
                case TokenType.UIntType: typeName = "uint"; break;
                case TokenType.ULongType: typeName = "ulong"; break;
                case TokenType.ObjectType: typeName = "object"; break;
                case TokenType.ListType: typeName = "list"; break;
                case TokenType.DictType: typeName = "dict"; break;
                case TokenType.Identifier: typeName = typeToken.Value; break;
                default:
                    throw new ParseException(
                        $"Line {typeToken.Line}: Expected type name, got '{typeToken.Value}'");
            }

            Advance();

            // array types: int[]
            while (Match(TokenType.LeftBracket))
            {
                Consume(TokenType.RightBracket, "Expected ']' in array type");
                typeName += "[]";
            }

            // generics: list<int>, dict<string, int>
            if (Match(TokenType.LessThan))
            {
                var innerType = ParseTypeName();
                while (Match(TokenType.Comma))
                    innerType += ", " + ParseTypeName();
                Consume(TokenType.GreaterThan, "Expected '>' in generic type");
                typeName += $"<{innerType}>";
            }

            return typeName;
        }

        private Statement ParseNamespace()
        {
            var nameToken = Consume(TokenType.Identifier, "Expected namespace name");
            string name = nameToken.Value;

            while (Match(TokenType.Dot))
                name += "." + Consume(TokenType.Identifier, "Expected namespace part").Value;

            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' after namespace name");

            var body = new List<Statement>();
            SkipNewlines();

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                var stmt = ParseDeclaration();
                if (stmt != null) body.Add(stmt);
                SkipNewlines();
            }

            Consume(TokenType.RightBrace, "Expected '}' to close namespace");
            return new NamespaceDecl(name, body) { Line = nameToken.Line };
        }

        private Statement ParseImport()
        {
            var moduleToken = Consume(TokenType.Identifier, "Expected module name");
            string module = moduleToken.Value;

            while (Match(TokenType.Dot))
                module += "." + Consume(TokenType.Identifier, "Expected module part").Value;

            string alias = null;
            if (Check(TokenType.Identifier) && Peek(1).Type != TokenType.Dot &&
                Previous.Value != "as" && Current.Value == "as")
            {
                Advance();
                alias = Consume(TokenType.Identifier, "Expected alias name").Value;
            }

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after import");

            return new ImportStmt(module, alias) { Line = moduleToken.Line };
        }

        private Statement ParseClass()
        {
            var nameToken = Consume(TokenType.Identifier, "Expected class name");
            var classDecl = new ClassDecl { Name = nameToken.Value, Line = nameToken.Line };

            // inheritance: class Dog : Animal   OR   class Dog extends Animal
            bool hasBase = false;
            if (Check(TokenType.Colon)) { Advance(); hasBase = true; }
            else if (Check(TokenType.Identifier) && Current.Value == "extends")
            { Advance(); hasBase = true; }

            if (hasBase)
            {
                classDecl.BaseClass = Consume(TokenType.Identifier, "Expected base class name").Value;
                while (Match(TokenType.Comma))
                    classDecl.Interfaces.Add(Consume(TokenType.Identifier, "Expected interface name").Value);
            }

            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' after class declaration");
            SkipNewlines();

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                var member = ParseClassMember();
                if (member != null) classDecl.Members.Add(member);
                SkipNewlines();
            }

            Consume(TokenType.RightBrace, "Expected '}' to close class");
            return classDecl;
        }

        private Statement ParseClassMember()
        {
            bool isStatic = false;
            bool isAsync = false;

            while (Match(TokenType.Public, TokenType.Private, TokenType.Protected,
                         TokenType.Internal, TokenType.Static, TokenType.Async,
                         TokenType.Virtual, TokenType.Override, TokenType.Abstract))
            {
                switch (Previous.Type)
                {
                    case TokenType.Static: isStatic = true; break;
                    case TokenType.Async: isAsync = true; break;
                }
            }

            if (IsInteropAttribute())
                return ParseInteropDeclaration();

            if (Match(TokenType.Func))
            {
                var func = ParseFunction();
                if (func is FunctionDecl fd)
                {
                    fd.IsStatic = isStatic;
                    fd.IsAsync = isAsync;
                }
                return func;
            }

            if (Match(TokenType.Delegate))
                return ParseDelegateDeclaration();

            if (Match(TokenType.Event))
                return ParseEventDeclaration(isStatic);

            // var Name { get; set; } — property (checked before ordinary fields)
            if (Check(TokenType.Var) || Check(TokenType.Let) || Check(TokenType.Const))
            {
                Advance(); // var/let/const
                var propName = Consume(TokenType.Identifier, "Expected member name");

                // property accessors may start on the next line:
                //   var name { get; set; }   |   var name\n{
                if (Check(TokenType.LeftBrace) ||
                    ((Check(TokenType.Newline) || Check(TokenType.Semicolon)) &&
                     Peek(1).Type == TokenType.LeftBrace))
                {
                    SkipNewlines();
                    return ParseProperty(propName, null, isStatic);
                }

                _position -= 2; // back up past name AND 'var'; field parsing re-consumes both
                var varMember = ParseVariableOrStatement();
                if (varMember is VariableDecl memberDecl)
                    memberDecl.IsStatic = isStatic;
                else if (varMember is MultiVariableDecl multiDecl)
                    foreach (var d in multiDecl.Declarations) d.IsStatic = isStatic;
                return varMember;
            }

            // operator overload: `operator +(...)`, `Vector operator +(...)`,
            // `bool operator ==(...)` — with or without a return type
            if (Check(TokenType.Operator) ||
                ((Check(TokenType.Identifier) || IsTypeStart()) &&
                 Peek(1).Type == TokenType.Operator))
            {
                string returnType = null;
                if (!Check(TokenType.Operator))
                    returnType = ParseTypeName();   // `Vector` / `bool` / `void` ...

                Consume(TokenType.Operator, "Expected 'operator'");
                return ParseOperatorDeclaration(returnType);
            }

            // C#-style members: `void Move(int x) {}`, `bool IsMoving;`,
            // `string Name { get; set; }`, `int num = 3;`, `Movement dir;`
            if (IsTypeStart() ||
                (Check(TokenType.Identifier) && Peek(1).Type == TokenType.Identifier))
            {
                return ParseCSharpStyleMember(isStatic);
            }

            // apply captured modifiers to member variables too (static var x = ...)
            var varMember2 = ParseVariableOrStatement();
            if (varMember2 is VariableDecl memberDecl2)
                memberDecl2.IsStatic = isStatic;
            else if (varMember2 is MultiVariableDecl multiDecl2)
                foreach (var d in multiDecl2.Declarations) d.IsStatic = isStatic;
            return varMember2;
        }

        // ---- C#-style class members ----

        private bool IsTypeStart()
        {
            // IntType..DictType is contiguous in TokenType
            return Current.Type >= TokenType.IntType && Current.Type <= TokenType.DictType;
        }

        private Statement ParseCSharpStyleMember(bool isStatic)
        {
            var type = ParseTypeName();
            var nameToken = Consume(TokenType.Identifier, "Expected member name after type");

            // typed property: string Name { get; set; }   (brace may be on a new line)
            if (Check(TokenType.LeftBrace) ||
                ((Check(TokenType.Newline) || Check(TokenType.Semicolon)) &&
                 Peek(1).Type == TokenType.LeftBrace))
            {
                SkipNewlines();
                return ParseProperty(nameToken, type, isStatic);
            }

            // C#-style method: void Move(int steps) { ... }
            if (Check(TokenType.LeftParen))
            {
                var func = new FunctionDecl
                { Name = nameToken.Value, ReturnType = type, Line = nameToken.Line, IsStatic = isStatic };

                Advance(); // (
                ParseParameterList(func, allowCSharpStyle: true);
                Consume(TokenType.RightParen, "Expected ')' after parameters");

                if (Match(TokenType.Arrow) || Match(TokenType.Colon))
                    func.ReturnType = ParseTypeName();

                SkipNewlines();
                Consume(TokenType.LeftBrace, "Expected '{' for method body");
                func.Body = ParseBlockStatements();
                Consume(TokenType.RightBrace, "Expected '}' to close method body");
                return func;
            }

            // typed field: bool IsMoving;  |  int num = 3;
            Expression init = null;
            if (Match(TokenType.Assign))
                init = ParseExpression();

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after field declaration");

            return new VariableDecl(nameToken.Value, type, "var", init)
            { Line = nameToken.Line, IsStatic = isStatic };
        }

        private Statement ParseProperty(Token nameToken, string propertyType, bool isStatic)
        {
            var prop = new PropertyDecl
            {
                Name = nameToken.Value,
                PropertyType = propertyType,
                IsStatic = isStatic,
                Line = nameToken.Line
            };

            Consume(TokenType.LeftBrace, "Expected '{' to open property accessors");
            SkipNewlines();

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                SkipNewlines();
                if (Check(TokenType.RightBrace)) break;

                Match(TokenType.Public, TokenType.Private, TokenType.Protected,
                      TokenType.Internal, TokenType.Static);

                if (Match(TokenType.Get))
                {
                    prop.HasGetter = true;
                    if (Check(TokenType.Semicolon))
                    {
                        Advance(); // auto-implemented: get;
                    }
                    else if (Match(TokenType.FatArrow))
                    {
                        var getterExpr = ParseExpression();
                        var getter = new FunctionDecl
                        {
                            Name = $"get_{nameToken.Value}",
                            ReturnType = propertyType,
                            IsStatic = isStatic,
                            Line = nameToken.Line
                        };
                        getter.Body.Add(new ReturnStmt(getterExpr));
                        prop.GetterBody = getter;
                        if (!IsStatementTerminator())
                            Consume(TokenType.Semicolon, "Expected ';' after expression-bodied getter");
                    }
                    else
                    {
                        SkipNewlines();
                        if (Check(TokenType.LeftBrace))
                        {
                            Advance();
                            var getter = new FunctionDecl
                            {
                                Name = $"get_{nameToken.Value}",
                                ReturnType = propertyType,
                                IsStatic = isStatic,
                                Line = nameToken.Line
                            };
                            getter.Body = ParseBlockStatements();
                            Consume(TokenType.RightBrace, "Expected '}' to close getter body");
                            prop.GetterBody = getter;
                        }
                        else
                        {
                            Consume(TokenType.Semicolon, "Expected ';' or '{' after 'get'");
                        }
                    }
                }
                else if (Match(TokenType.Set))
                {
                    prop.HasSetter = true;
                    if (Check(TokenType.Semicolon))
                    {
                        Advance(); // auto-implemented: set;
                    }
                    else if (Match(TokenType.FatArrow))
                    {
                        var target = ParseExpression();
                        Consume(TokenType.Assign, "Expected '=' in expression-bodied setter");
                        var valueExpr = ParseExpression();

                        var setter = new FunctionDecl
                        {
                            Name = $"set_{nameToken.Value}",
                            ReturnType = "void",
                            IsStatic = isStatic,
                            Line = nameToken.Line
                        };
                        setter.Parameters.Add(new Parameter("value", propertyType ?? "dynamic"));
                        setter.Body.Add(new ExpressionStmt(new AssignExpr(target, "=", valueExpr)));
                        prop.SetterBody = setter;
                        if (!IsStatementTerminator())
                            Consume(TokenType.Semicolon, "Expected ';' after expression-bodied setter");
                    }
                    else
                    {
                        SkipNewlines();
                        if (Check(TokenType.LeftBrace))
                        {
                            Advance();
                            var setter = new FunctionDecl
                            {
                                Name = $"set_{nameToken.Value}",
                                ReturnType = "void",
                                IsStatic = isStatic,
                                Line = nameToken.Line
                            };
                            setter.Parameters.Add(new Parameter("value", propertyType ?? "dynamic"));
                            setter.Body = ParseBlockStatements();
                            Consume(TokenType.RightBrace, "Expected '}' to close setter body");
                            prop.SetterBody = setter;
                        }
                        else
                        {
                            Consume(TokenType.Semicolon, "Expected ';' or '{' after 'set'");
                        }
                    }
               }
                else
                {
                    throw new ParseException($"Line {Current.Line}: Expected 'get' or 'set' in property '{nameToken.Value}'");
                }

                SkipNewlines();
            }

            Consume(TokenType.RightBrace, "Expected '}' to close property");
            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after property");
            return prop;
        }

        private Statement ParseDelegateDeclaration(bool isStatic = false)
        {
            var returnType = ParseTypeName();
            var nameToken = Consume(TokenType.Identifier, "Expected delegate name");
            var decl = new DelegateDecl
            { Name = nameToken.Value, ReturnType = returnType, Line = nameToken.Line };

            Consume(TokenType.LeftParen, "Expected '(' after delegate name");
            var shell = new FunctionDecl { Name = nameToken.Value };
            ParseParameterList(shell, allowCSharpStyle: true);
            Consume(TokenType.RightParen, "Expected ')' after delegate parameters");
            foreach (var p in shell.Parameters)
                decl.Parameters.Add(p);

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after delegate declaration");
            return decl;
        }

        private Statement ParseEventDeclaration(bool isStatic = false)
        {
            var delegateType = ParseTypeName();
            var nameToken = Consume(TokenType.Identifier, "Expected event name");

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after event declaration");

            return new EventDecl
            {
                Name = nameToken.Value,
                DelegateType = delegateType,
                IsStatic = isStatic,
                Line = nameToken.Line
            };
        }

        private static readonly Dictionary<TokenType, string> OperatorSymbols =
            new Dictionary<TokenType, string>
        {
            [TokenType.Plus] = "+", [TokenType.Minus] = "-", [TokenType.Star] = "*",
            [TokenType.Slash] = "/", [TokenType.Percent] = "%",
            [TokenType.Equals] = "==", [TokenType.NotEquals] = "!=",
            [TokenType.LessThan] = "<", [TokenType.GreaterThan] = ">",
            [TokenType.LessEqual] = "<=", [TokenType.GreaterEqual] = ">=",
            [TokenType.Not] = "!", [TokenType.PlusPlus] = "++", [TokenType.MinusMinus] = "--",
            [TokenType.BitAnd] = "&", [TokenType.BitOr] = "|", [TokenType.BitXor] = "^",
            [TokenType.BitNot] = "~", [TokenType.ShiftLeft] = "<<", [TokenType.ShiftRight] = ">>"
        };

        private Statement ParseOperatorDeclaration(string returnType)
        {
            var symbolToken = Current;
            if (!OperatorSymbols.TryGetValue(symbolToken.Type, out var symbol))
                throw new ParseException(
                    $"Line {symbolToken.Line}: Expected an overloadable operator after 'operator', got '{symbolToken.Value}'");
            Advance();

            var func = new FunctionDecl
            {
                Name = $"operator {symbol}",
                ReturnType = returnType,
                IsOperator = true,
                OperatorSymbol = symbol,
                IsStatic = true, // C# operators are always static
                Line = symbolToken.Line
            };

            Consume(TokenType.LeftParen, "Expected '(' after operator");
            ParseParameterList(func, allowCSharpStyle: true);
            Consume(TokenType.RightParen, "Expected ')' after operator parameters");

            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' for operator body");
            func.Body = ParseBlockStatements();
            Consume(TokenType.RightBrace, "Expected '}' to close operator body");
            return func;
        }

        private Statement ParseStruct()
        {
            var nameToken = Consume(TokenType.Identifier, "Expected struct name");
            var structDecl = new StructDecl { Name = nameToken.Value, Line = nameToken.Line };

            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' after struct name");
            SkipNewlines();

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                SkipNewlines();
                if (Check(TokenType.RightBrace)) break;

                var fieldName = Consume(TokenType.Identifier, "Expected field name").Value;
                string fieldType = "dynamic";

                if (Match(TokenType.Colon))
                    fieldType = ParseTypeName();

                Expression init = null;
                if (Match(TokenType.Assign))
                    init = ParseExpression();

                structDecl.Fields.Add(new VariableDecl(fieldName, fieldType, "var", init));

                if (!IsStatementTerminator())
                    Consume(TokenType.Semicolon, "Expected ';' after field");
                SkipNewlines();
            }

            Consume(TokenType.RightBrace, "Expected '}' to close struct");
            return structDecl;
        }

        private Statement ParseEnum()
        {
            var nameToken = Consume(TokenType.Identifier, "Expected enum name");
            var enumDecl = new EnumDecl { Name = nameToken.Value, Line = nameToken.Line };

            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' after enum name");
            SkipNewlines();

            long defaultValue = 0;

            // ---- Phase 1: the member list (Up, Down, Left, Right) ----
            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                SkipNewlines();
                if (Check(TokenType.RightBrace)) break;

                // A member name must be followed by ',', '=', ';', newline or '}'.
                // Anything else (a modifier, type or 'void') starts Phase 2.
                if (!Check(TokenType.Identifier) ||
                    (Peek(1).Type != TokenType.Comma &&
                     Peek(1).Type != TokenType.Assign &&
                     Peek(1).Type != TokenType.Semicolon &&
                     Peek(1).Type != TokenType.Newline &&
                     Peek(1).Type != TokenType.RightBrace))
                    break;

                var memberName = Consume(TokenType.Identifier, "Expected enum member name").Value;
                long value = defaultValue;

                if (Match(TokenType.Assign))
                {
                    if (Check(TokenType.Minus) && Peek(1).Type == TokenType.IntLiteral)
                    {
                        Advance();
                        value = -Convert.ToInt64(Advance().Literal);
                    }
                    else
                    {
                        var valueToken = Consume(TokenType.IntLiteral, "Expected integer value for enum member");
                        value = Convert.ToInt64(valueToken.Literal);
                    }
                }

                enumDecl.Members.Add(Tuple.Create(memberName, value));
                defaultValue = value + 1;

                if (!Match(TokenType.Comma))
                {
                    SkipNewlines();
                    Match(TokenType.Semicolon); // optional trailing ';'
                    if (!Check(TokenType.RightBrace) && !Check(TokenType.Comma))
                        break;
                }
                SkipNewlines();
            }

            SkipNewlines();
            Match(TokenType.Semicolon);
            SkipNewlines();

            // ---- Phase 2: methods / static fields / properties in the enum body
            // (Nexus extension: enums can carry static members like a class) ----
            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                SkipNewlines();
                if (Check(TokenType.RightBrace)) break;

                var member = ParseClassMember();
                if (member != null)
                {
                    // everything inside an enum behaves as static
                    switch (member)
                    {
                        case FunctionDecl f: f.IsStatic = true; break;
                        case VariableDecl v: v.IsStatic = true; break;
                        case PropertyDecl p: p.IsStatic = true; break;
                    }
                    enumDecl.MemberStatements.Add(member);
                }
                SkipNewlines();
            }

            Consume(TokenType.RightBrace, "Expected '}' to close enum");
            return enumDecl;
        }

        private Statement ParseFunction()
        {
            var nameToken = Consume(TokenType.Identifier, "Expected function name");
            var func = new FunctionDecl { Name = nameToken.Value, Line = nameToken.Line };

            Consume(TokenType.LeftParen, "Expected '(' after function name");
            ParseParameterList(func, allowCSharpStyle: true);
            Consume(TokenType.RightParen, "Expected ')' after parameters");

            if (Match(TokenType.Arrow) || Match(TokenType.Colon))
                func.ReturnType = ParseTypeName();

            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' for function body");

            func.Body = ParseBlockStatements();

            Consume(TokenType.RightBrace, "Expected '}' to close function body");
            return func;
        }

        /// <summary>Shared parameter-list parser. Supports both styles:
        /// `func add(a, b)` / `func add(a: int, b: int)` and C#-style
        /// `func add(int a, int b)`. Assumes Current is the first param or ')';
        /// consumes up to (but not including) ')'.</summary>
        private void ParseParameterList(FunctionDecl func, bool allowCSharpStyle = false)
        {
            if (Check(TokenType.RightParen)) return;

            do
            {
                SkipNewlines();
                bool isRef = Match(TokenType.Ref);
                bool isOut = Match(TokenType.Out);
                bool isParams = Match(TokenType.Params);
                SkipNewlines();

                string paramName;
                string paramType = "dynamic";

                bool csharpTyped = allowCSharpStyle && IsTypeStart();
                bool identifierTyped = allowCSharpStyle &&
                                       Check(TokenType.Identifier) &&
                                       Peek(1).Type == TokenType.Identifier;

                if (csharpTyped || identifierTyped)
                {
                    // C#-style: `int steps`
                    paramType = ParseTypeName();
                    paramName = Consume(TokenType.Identifier, "Expected parameter name").Value;
                }
                else
                {
                    paramName = Consume(TokenType.Identifier, "Expected parameter name").Value;
                }

                var param = new Parameter(paramName, paramType)
                { IsRef = isRef, IsOut = isOut, IsParams = isParams };

                if (Match(TokenType.Colon))          // name: type style
                    param.Type = ParseTypeName();

                if (Match(TokenType.Assign))
                    param.DefaultValue = ParseExpression();

                func.Parameters.Add(param);
            } while (Match(TokenType.Comma));

            SkipNewlines();
        }

        private List<Statement> ParseBlockStatements()
        {
            var statements = new List<Statement>();
            SkipNewlines();

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                var stmt = ParseDeclaration();
                if (stmt != null) statements.Add(stmt);
                SkipNewlines();
            }

            return statements;
        }

        // ============ INTEROP ============
        private Statement ParseInteropDeclaration()
        {
            var interopTypeToken = Advance();
            string interopType = interopTypeToken.Value;

            var libraryToken = Consume(TokenType.StringLiteral,
                $"Expected library string after [{interopType}]");
            string library = libraryToken.Value ?? "";

            // strip extra options ("lib.so", entry="x" → "lib.so")
            int comma = library.IndexOf(',');
            if (comma >= 0) library = library.Substring(0, comma).Trim();
            library = library.Trim('"');

            SkipNewlines();
            Match(TokenType.Extern);

            string returnType = "void";
            if (!Check(TokenType.Identifier) || Keywords.IsKeyword(Current.Value))
                returnType = ParseTypeName();

            var nameToken = Consume(TokenType.Identifier,
                $"Expected method name after [{interopType}]");

            var declaration = new InteropDeclaration
            {
                InteropType = interopType,
                Library = library,
                ReturnType = returnType,
                MethodName = nameToken.Value,
                Line = interopTypeToken.Line
            };

            Consume(TokenType.LeftParen, $"Expected '(' after method name '{nameToken.Value}'");

            if (!Check(TokenType.RightParen))
            {
                do
                {
                    SkipNewlines();
                    var paramName = Consume(TokenType.Identifier, "Expected parameter name");
                    var param = new Parameter(paramName.Value, "dynamic");
                    if (Match(TokenType.Colon))
                        param.Type = ParseTypeName();
                    declaration.Parameters.Add(param);
                } while (Match(TokenType.Comma));
            }

            Consume(TokenType.RightParen, "Expected ')' after parameters");

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after interop declaration");

            return declaration;
        }

        private Statement ParseExternDeclaration()
        {
            if (Match(TokenType.Func))
                return ParseFunction();

            string returnType = ParseTypeName();
            var nameToken = Consume(TokenType.Identifier, "Expected extern variable name");

            var decl = new InteropDeclaration
            {
                InteropType = "ExternVariable",
                ReturnType = returnType,
                MethodName = nameToken.Value,
                Line = nameToken.Line
            };

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after extern declaration");

            return decl;
        }

        // ============ STATEMENTS ============
        private Statement ParseStatement()
        {
            SkipNewlines();

            if (Match(TokenType.LeftBrace)) return ParseBlock();
            if (Match(TokenType.If)) return ParseIf();
            if (Match(TokenType.While)) return ParseWhile(false);
            if (Match(TokenType.Do)) return ParseWhile(true);
            if (Match(TokenType.For)) return ParseFor();
            if (Match(TokenType.Foreach)) return ParseForeach();
            if (Match(TokenType.Return)) return ParseReturn();
            if (Match(TokenType.Break)) return ParseSimpleStmt<BreakStmt>();
            if (Match(TokenType.Continue)) return ParseSimpleStmt<ContinueStmt>();
            if (Match(TokenType.Asm)) return ParseAsmBlock();
            if (Match(TokenType.Try)) return ParseTryCatch();
            if (Match(TokenType.Throw)) return ParseThrow();
            if (Match(TokenType.Switch)) return ParseSwitch();
            if (Match(TokenType.Print, TokenType.Println)) return ParsePrintStatement();

            if (Match(TokenType.Var, TokenType.Let, TokenType.Const))
            {
                _position--; // back up; ParseVariableOrStatement re-consumes
                return ParseVariableOrStatement();
            }

            // typed local declarations: `int x = 5;`, `Movement dir;`,
            // `Operation op = Add;`, `string greeting;`
            if ((IsTypeStart() || Check(TokenType.Identifier)) &&
                Peek(1).Type == TokenType.Identifier)
            {
                var afterName = Peek(2).Type;
                if (afterName == TokenType.Assign || afterName == TokenType.Semicolon ||
                    afterName == TokenType.Newline || afterName == TokenType.RightBrace)
                {
                    return ParseTypedDeclaration();
                }
            }

            return ParseExpressionStatement();
        }

        private Statement ParseTypedDeclaration()
        {
            var type = ParseTypeName();
            var nameToken = Consume(TokenType.Identifier, "Expected variable name");

            Expression init = null;
            if (Match(TokenType.Assign))
                init = ParseExpression();

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after variable declaration");

            return new VariableDecl(nameToken.Value, type, "var", init)
            { Line = nameToken.Line };
        }

        private Statement ParseSimpleStmt<T>() where T : Statement, new()
        {
            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, $"Expected ';' after {typeof(T).Name}");
            var stmt = new T { Line = Previous.Line };
            return stmt;
        }

        private Statement ParseBlock()
        {
            var statements = new List<Statement>();
            SkipNewlines();

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                var stmt = ParseDeclaration();
                if (stmt != null) statements.Add(stmt);
                SkipNewlines();
            }

            Consume(TokenType.RightBrace, "Expected '}' to close block");
            return new BlockStmt(statements);
        }

        private Statement ParsePrintStatement()
        {
            var keyword = Previous; // 'print' or 'println'
            bool newline = keyword.Type == TokenType.Println;
            var values = new List<Expression>();

            // Parenthesized form: println("a", b + 1);  (backwards compatible)
            if (Match(TokenType.LeftParen))
            {
                SkipNewlines();
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        SkipNewlines();
                        if (Check(TokenType.RightParen)) break;
                        values.Add(ParseExpression());
                    } while (Match(TokenType.Comma));
                }
                SkipNewlines();
                Consume(TokenType.RightParen, "Expected ')' after print arguments");
            }
            else
            {
                // Bare statement form: println "a", b;
                if (!IsStatementTerminator() && !Check(TokenType.RightBrace))
                {
                    do
                    {
                        values.Add(ParseExpression());
                    } while (Match(TokenType.Comma));
                }
            }

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after print statement");

            return new PrintStmt(values, newline) { Line = keyword.Line };
        }

        private Statement ParseIf()
        {
            Consume(TokenType.LeftParen, "Expected '(' after 'if'");
            var condition = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after condition");

            SkipNewlines();
            var thenBranch = ParseStatement();

            Statement elseBranch = null;
            SkipNewlines();
            if (Match(TokenType.Else))
            {
                SkipNewlines();
                elseBranch = ParseStatement();
            }

            return new IfStmt(condition, thenBranch, elseBranch);
        }

        private Statement ParseWhile(bool isDoWhile)
        {
            if (isDoWhile)
            {
                var body = ParseStatement();
                SkipNewlines();
                Consume(TokenType.While, "Expected 'while' after do block");
                Consume(TokenType.LeftParen, "Expected '(' after 'while'");
                var condition = ParseExpression();
                Consume(TokenType.RightParen, "Expected ')' after condition");
                if (!IsStatementTerminator())
                    Consume(TokenType.Semicolon, "Expected ';' after do-while");
                return new WhileStmt(condition, body, true);
            }

            Consume(TokenType.LeftParen, "Expected '(' after 'while'");
            var cond = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after condition");

            SkipNewlines();
            var loopBody = ParseStatement();

            return new WhileStmt(cond, loopBody);
        }

        private Statement ParseFor()
        {
            Consume(TokenType.LeftParen, "Expected '(' after 'for'");

            Statement initializer = null;
            if (!Check(TokenType.Semicolon))
            {
                if (Match(TokenType.Var, TokenType.Let, TokenType.Const))
                {
                    _position--;
                    initializer = ParseVariableOrStatement();
                }
                else if ((IsTypeStart() ||
                          (Check(TokenType.Identifier) && Peek(1).Type == TokenType.Identifier)) &&
                         Peek(1).Type == TokenType.Identifier &&
                         Peek(2).Type == TokenType.Assign)
                {
                    // C#-style for-initializer: for (int i = 0; ...)
                    initializer = ParseTypedDeclaration();
                }
                else
                {
                    initializer = ParseExpressionStatement();
                }
            }

            // consume the ';' after the initializer (variable/expr initializers
            // stop at it without consuming)
            if (Check(TokenType.Semicolon))
                Advance();

            Expression condition = null;
            if (!Check(TokenType.Semicolon))
                condition = ParseExpression();

            // consume the ';' after the condition
            if (Check(TokenType.Semicolon))
                Advance();

            Expression increment = null;
            if (!Check(TokenType.RightParen))
                increment = ParseExpression();

            Consume(TokenType.RightParen, "Expected ')' after for clauses");

            SkipNewlines();
            var body = ParseStatement();

            return new ForStmt(initializer, condition, increment, body);
        }

        private Statement ParseForeach()
        {
            Consume(TokenType.LeftParen, "Expected '(' after 'foreach'");
            SkipNewlines();
            Match(TokenType.Var, TokenType.Let, TokenType.Const);   // foreach (var x in ...)
            var varName = Consume(TokenType.Identifier, "Expected variable name").Value;
            Consume(TokenType.In, "Expected 'in' in foreach");
            var iterable = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after foreach");

            SkipNewlines();
            var body = ParseStatement();

            return new ForeachStmt(varName, iterable, body);
        }

        private Statement ParseReturn()
        {
            Expression value = null;
            if (!IsStatementTerminator() && !Check(TokenType.RightBrace))
                value = ParseExpression();

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after return");

            return new ReturnStmt(value);
        }

        private Statement ParseExpressionStatement()
        {
            var expr = ParseExpression();

            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after expression");

            return new ExpressionStmt(expr);
        }

        private Statement ParseThrow()
        {
            var value = ParseExpression();
            if (!IsStatementTerminator())
                Consume(TokenType.Semicolon, "Expected ';' after throw");
            return new ThrowStmt(value);
        }

        private Statement ParseTryCatch()
        {
            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' after 'try'");
            var tryBlock = new BlockStmt(ParseBlockStatements());
            Consume(TokenType.RightBrace, "Expected '}' to close try block");

            string catchVar = null;
            Statement catchBlock = null;

            SkipNewlines();
            if (Match(TokenType.Catch))
            {
                if (Match(TokenType.LeftParen))
                {
                    catchVar = Consume(TokenType.Identifier, "Expected catch variable name").Value;
                    Consume(TokenType.RightParen, "Expected ')' after catch variable");
                }
                SkipNewlines();
                Consume(TokenType.LeftBrace, "Expected '{' after 'catch'");
                catchBlock = new BlockStmt(ParseBlockStatements());
                Consume(TokenType.RightBrace, "Expected '}' to close catch block");
            }

            Statement finallyBlock = null;
            SkipNewlines();
            if (Match(TokenType.Finally))
            {
                SkipNewlines();
                Consume(TokenType.LeftBrace, "Expected '{' after 'finally'");
                finallyBlock = new BlockStmt(ParseBlockStatements());
                Consume(TokenType.RightBrace, "Expected '}' to close finally block");
            }

            return new TryCatchStmt(tryBlock, catchVar, catchBlock, finallyBlock);
        }

        private Statement ParseSwitch()
        {
            Consume(TokenType.LeftParen, "Expected '(' after 'switch'");
            var subject = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after switch subject");

            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' after switch");
            SkipNewlines();

            var switchStmt = new SwitchStmt(subject);
            Statement defaultCase = null;

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                SkipNewlines();

                if (Match(TokenType.Case))
                {
                    var caseValue = ParseExpression();
                    Consume(TokenType.Colon, "Expected ':' after case value");
                    SkipNewlines();

                    var caseBody = new List<Statement>();
                    while (!Check(TokenType.Case) && !Check(TokenType.Default) &&
                           !Check(TokenType.RightBrace) && !IsAtEnd())
                    {
                        var stmt = ParseDeclaration();
                        if (stmt != null) caseBody.Add(stmt);
                        SkipNewlines();
                    }

                    switchStmt.Cases.Add(new SwitchCase { Value = caseValue, Body = caseBody });
                }
                else if (Match(TokenType.Default))
                {
                    Consume(TokenType.Colon, "Expected ':' after 'default'");
                    SkipNewlines();

                    var defaultBody = new List<Statement>();
                    while (!Check(TokenType.Case) && !Check(TokenType.Default) &&
                           !Check(TokenType.RightBrace) && !IsAtEnd())
                    {
                        var stmt = ParseDeclaration();
                        if (stmt != null) defaultBody.Add(stmt);
                        SkipNewlines();
                    }

                    defaultCase = new BlockStmt(defaultBody);
                }
                else
                {
                    break;
                }
            }

            Consume(TokenType.RightBrace, "Expected '}' to close switch");
            switchStmt.DefaultCase = defaultCase;
            return switchStmt;
        }

        // ============ ASM ============
        private Statement ParseAsmBlock()
        {
            var asmBlock = new AsmBlock(Previous.Line) { IsInline = false };

            // Statement inline form: asm("mov eax, 5");
            if (Match(TokenType.LeftParen))
            {
                asmBlock.IsInline = true;
                var codeToken = Consume(TokenType.StringLiteral, "Expected assembly code string");
                Consume(TokenType.RightParen, "Expected ')' after inline assembly");
                ParseAsmText(codeToken.Value, asmBlock);
                return asmBlock;
            }

            ParseAsmBlockBody(asmBlock);
            return asmBlock;
        }

        /// <summary>Shared by statement asm blocks and asm modules in expression
        /// position. Expects Current at '{' (newlines allowed before it).</summary>
        private void ParseAsmBlockBody(AsmBlock asmBlock)
        {
            SkipNewlines();
            Consume(TokenType.LeftBrace, "Expected '{' after 'asm'");
            SkipNewlines();

            while (!Check(TokenType.RightBrace) && !IsAtEnd())
            {
                SkipNewlines();
                if (Check(TokenType.RightBrace)) break;

                // Label:  name:
                if (Check(TokenType.Identifier) && Peek(1).Type == TokenType.Colon)
                {
                    string label = Advance().Value;
                    Advance(); // ':'
                    asmBlock.Labels.Add(label);
                    asmBlock.LabelPositions[label] = asmBlock.Instructions.Count;
                    continue;
                }

                // Data: db name = "..." | dd name = 42 | ...
                if (Check(TokenType.Db) || Check(TokenType.Dw) ||
                    Check(TokenType.Dd) || Check(TokenType.Dq))
                {
                    string directive = Advance().Value;
                    var dataDef = new AsmDataDef { Directive = directive };

                    if (Check(TokenType.Identifier))
                        dataDef.Name = Advance().Value;

                    Consume(TokenType.Assign, "Expected '=' after data directive");

                    do
                    {
                        if (Check(TokenType.StringLiteral))
                            dataDef.Values.Add(Advance().Value);
                        else if (Check(TokenType.IntLiteral))
                            dataDef.Values.Add(Advance().Literal);
                        else if (Check(TokenType.CharLiteral))
                            dataDef.Values.Add(Advance().Literal);
                        else if (Check(TokenType.Minus) && Peek(1).Type == TokenType.IntLiteral)
                        {
                            Advance();
                            dataDef.Values.Add(-Convert.ToInt64(Advance().Literal));
                        }
                    } while (Match(TokenType.Comma));

                    asmBlock.DataDefinitions.Add(dataDef);
                    continue;
                }

                var instruction = ParseAsmInstruction();
                if (instruction != null)
                    asmBlock.Instructions.Add(instruction);
            }

            SkipNewlines();
            Consume(TokenType.RightBrace, "Expected '}' to close asm block");
        }

        private void ParseAsmText(string text, AsmBlock block)
        {
            var lines = text.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(";") || trimmed.StartsWith("//"))
                    continue;

                if (trimmed.EndsWith(":"))
                {
                    string label = trimmed.TrimEnd(':');
                    block.Labels.Add(label);
                    block.LabelPositions[label] = block.Instructions.Count;
                    continue;
                }

                var parts = trimmed.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1)
                {
                    string mnemonic = parts[0];
                    string op1 = parts.Length > 1 ? parts[1].TrimEnd(',') : null;
                    string op2 = parts.Length > 2 ? parts[2] : null;

                    block.Instructions.Add(new AsmInstruction(mnemonic, op1, op2));
                }
            }
        }

        private AsmInstruction ParseAsmInstruction()
        {
            var mnemonicToken = Current;

            // NASM mnemonics lex as ordinary identifiers (mov/add/call/... are NOT
            // reserved words, so user code can use them as names). `int` is special:
            // it lexes as the type keyword, so `int 21h` needs its own case.
            string mnemonic = mnemonicToken.Type switch
            {
                TokenType.Identifier => mnemonicToken.Value.ToLowerInvariant(),
                TokenType.IntType => "int",
                _ => null
            };

            if (mnemonic == null)
            {
                _errors.Add($"Line {mnemonicToken.Line}: Unknown assembly instruction '{mnemonicToken.Value}'");
                Advance();
                return null;
            }

            Advance(); // consume mnemonic

            string operand1 = null;
            string operand2 = null;

            if (!IsStatementTerminator() && !Check(TokenType.RightBrace) && !Check(TokenType.Newline))
                operand1 = ParseAsmOperand();

            if (Match(TokenType.Comma))
                operand2 = ParseAsmOperand();

            return new AsmInstruction(mnemonic, operand1, operand2, mnemonicToken.Line);
        }

        private string ParseAsmOperand()
        {
            var sb = new StringBuilder();

            // memory reference [address]
            if (Check(TokenType.LeftBracket))
            {
                sb.Append('[');
                Advance();

                int depth = 1;
                while (depth > 0 && !IsAtEnd())
                {
                    if (Check(TokenType.LeftBracket)) depth++;
                    if (Check(TokenType.RightBracket))
                    {
                        depth--;
                        if (depth == 0) { Advance(); break; }
                    }
                    sb.Append(Current.Value);
                    Advance();
                }

                sb.Append(']');
                return sb.ToString();
            }

            // registers
            if (Current.Type >= TokenType.Eax && Current.Type <= TokenType.Dh)
            {
                return Advance().Value;
            }

            if (Check(TokenType.IntLiteral))
                return Advance().Value;

            if (Check(TokenType.Identifier))
                return Advance().Value;

            if (Check(TokenType.StringLiteral))
                return $"\"{Advance().Value}\"";

            // compound expression operand: eax+4, 5*2, ebx*2+4 ...
            while (!IsStatementTerminator() && !Check(TokenType.Comma) &&
                   !Check(TokenType.RightBrace) && !Check(TokenType.RightParen))
            {
                sb.Append(Current.Value);
                Advance();
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        // ============ EXPRESSIONS ============
        private Expression ParseExpression() => ParseAssignmentExpression();

        private Expression ParseAssignmentExpression()
        {
            var expr = ParseTernary();

            if (Match(TokenType.Assign, TokenType.PlusAssign, TokenType.MinusAssign,
                      TokenType.StarAssign, TokenType.SlashAssign, TokenType.PercentAssign))
            {
                var op = Previous.Value;
                var value = ParseAssignmentExpression();
                return new AssignExpr(expr, op, value);
            }

            return expr;
        }

        private Expression ParseTernary()
        {
            var condition = ParseNullCoalescing();

            if (Match(TokenType.Question))
            {
                var thenExpr = ParseExpression();
                Consume(TokenType.Colon, "Expected ':' in ternary expression");
                var elseExpr = ParseTernary();
                return new TernaryExpr(condition, thenExpr, elseExpr);
            }

            return condition;
        }

        private Expression ParseNullCoalescing()
        {
            var left = ParseLogicalOr();
            while (Match(TokenType.QuestionQuestion))
                left = new BinaryExpr(left, "??", ParseLogicalOr());
            return left;
        }

        private Expression ParseLogicalOr()
        {
            var left = ParseLogicalAnd();
            while (Match(TokenType.OrOr))
                left = new BinaryExpr(left, "||", ParseLogicalAnd());
            return left;
        }

        private Expression ParseLogicalAnd()
        {
            var left = ParseBitwiseOr();
            while (Match(TokenType.AndAnd))
                left = new BinaryExpr(left, "&&", ParseBitwiseOr());
            return left;
        }

        private Expression ParseBitwiseOr()
        {
            var left = ParseBitwiseXor();
            while (Match(TokenType.BitOr))
                left = new BinaryExpr(left, "|", ParseBitwiseXor());
            return left;
        }

        private Expression ParseBitwiseXor()
        {
            var left = ParseBitwiseAnd();
            while (Match(TokenType.BitXor))
                left = new BinaryExpr(left, "^", ParseBitwiseAnd());
            return left;
        }

        private Expression ParseBitwiseAnd()
        {
            var left = ParseEquality();
            while (Match(TokenType.BitAnd))
                left = new BinaryExpr(left, "&", ParseEquality());
            return left;
        }

        private Expression ParseEquality()
        {
            var left = ParseComparison();
            while (Match(TokenType.Equals, TokenType.NotEquals))
                left = new BinaryExpr(left, Previous.Value, ParseComparison());
            return left;
        }

        private Expression ParseComparison()
        {
            var left = ParseShift();
            while (Match(TokenType.LessThan, TokenType.GreaterThan,
                         TokenType.LessEqual, TokenType.GreaterEqual))
                left = new BinaryExpr(left, Previous.Value, ParseShift());
            return left;
        }

        private Expression ParseShift()
        {
            var left = ParseRange();
            while (Match(TokenType.ShiftLeft, TokenType.ShiftRight))
                left = new BinaryExpr(left, Previous.Value, ParseRange());
            return left;
        }

        // 1..5 — inclusive range, binds tighter than comparisons,
        // looser than + - * / (so `1 + 2..5 * 2` is 3..10)
        private Expression ParseRange()
        {
            var left = ParseAdditive();
            if (Match(TokenType.DotDot))
            {
                var right = ParseAdditive();
                return new RangeExpr(left, right);
            }
            return left;
        }

        private Expression ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Match(TokenType.Plus, TokenType.Minus))
                left = new BinaryExpr(left, Previous.Value, ParseMultiplicative());
            return left;
        }

        private Expression ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Match(TokenType.Star, TokenType.Slash, TokenType.Percent))
                left = new BinaryExpr(left, Previous.Value, ParseUnary());
            return left;
        }

        private Expression ParseUnary()
        {
            if (Match(TokenType.Not))
                return new UnaryExpr("!", ParseUnary());

            if (Match(TokenType.Minus))
                return new UnaryExpr("-", ParseUnary());

            if (Match(TokenType.BitNot))
                return new UnaryExpr("~", ParseUnary());

            if (Match(TokenType.PlusPlus))
                return new UnaryExpr("++", ParseUnary(), false);

            if (Match(TokenType.MinusMinus))
                return new UnaryExpr("--", ParseUnary(), false);

            if (Match(TokenType.BitAnd))
                return new AddressOfExpr(ParseUnary());

            if (Match(TokenType.Star))
                return new PointerExpr(ParseUnary());

            return ParsePostfix();
        }

        private Expression ParsePostfix()
        {
            var expr = ParsePrimary();

            while (true)
            {
                if (Match(TokenType.LeftParen))
                {
                    var args = new List<Expression>();
                    SkipNewlines();

                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            SkipNewlines();
                            args.Add(ParseExpression());
                        } while (Match(TokenType.Comma));
                    }

                    SkipNewlines();
                    Consume(TokenType.RightParen, "Expected ')' after arguments");
                    expr = new CallExpr(expr, args);
                }
                else if (Match(TokenType.Dot))
                {
                    var member = ConsumeMemberName("Expected member name after '.'");
                    expr = new MemberAccessExpr(expr, member.Value);
                }
                else if (Check(TokenType.Question) && Peek(1).Type == TokenType.Dot)
                {
                    Advance(); Advance(); // ?.
                    var member = ConsumeMemberName("Expected member name after '?.'");
                    expr = new MemberAccessExpr(expr, member.Value) { NullConditional = true };
                }
                else if (Match(TokenType.LeftBracket))
                {
                    var index = ParseExpression();
                    Consume(TokenType.RightBracket, "Expected ']' after index");
                    expr = new IndexExpr(expr, index);
                }
                else if (Match(TokenType.PlusPlus))
                {
                    expr = new UnaryExpr("++", expr, true);
                }
                else if (Match(TokenType.MinusMinus))
                {
                    expr = new UnaryExpr("--", expr, true);
                }
                else
                {
                    break;
                }
            }

            return expr;
        }

        private Expression ParsePrimary()
        {
            // interpolated string: $"text {expr} more"
            if (Check(TokenType.Dollar) && Peek(1).Type == TokenType.StringLiteral)
            {
                Advance(); // $
                var raw = Advance().Value ?? "";
                return BuildInterpolatedString(raw);
            }

            if (Match(TokenType.IntLiteral))
                return new IntLiteralExpr(Convert.ToInt64(Previous.Literal));

            if (Match(TokenType.FloatLiteral))
                return new FloatLiteralExpr(Convert.ToDouble(Previous.Literal));

            if (Match(TokenType.StringLiteral))
                return new StringLiteralExpr(Convert.ToString(Previous.Literal));

            if (Match(TokenType.CharLiteral))
                return new CharLiteralExpr((char)Previous.Literal);

            if (Match(TokenType.BoolLiteral))
                return new BoolLiteralExpr(Previous.Value == "true");

            if (Match(TokenType.NullLiteral))
                return new NullLiteralExpr();

            if (Match(TokenType.This))
                return new ThisExpr();

            // super(args) — base constructor call
            if (Match(TokenType.Base))
            {
                Consume(TokenType.LeftParen, "Expected '(' after 'super'");
                var supArgs = new List<Expression>();
                SkipNewlines();
                if (!Check(TokenType.RightParen))
                {
                    do
                    {
                        SkipNewlines();
                        if (Check(TokenType.RightParen)) break;
                        supArgs.Add(ParseExpression());
                    } while (Match(TokenType.Comma));
                }
                SkipNewlines();
                Consume(TokenType.RightParen, "Expected ')' after super arguments");
                return new BaseInitCallExpr(supArgs);
            }

            // asm module in expression position: var m = asm { label: ... }
            if (Match(TokenType.Asm))
            {
                var moduleBlock = new AsmBlock(Previous.Line);
                ParseAsmBlockBody(moduleBlock);
                return new AsmModuleExpr(moduleBlock);
            }

            // lambda: (a, b) => ... | x => expr | () => ...   — tried before the
            // parenthesized-expression fallback; restores position if not a lambda
            var lambda = TryParseLambda();
            if (lambda != null)
                return lambda;

            if (Match(TokenType.LeftParen))
            {
                SkipNewlines();
                var expr = ParseExpression();
                SkipNewlines();
                Consume(TokenType.RightParen, "Expected ')'");
                return expr;
            }

            if (Match(TokenType.LeftBracket))
            {
                var elements = new List<Expression>();
                SkipNewlines();

                if (!Check(TokenType.RightBracket))
                {
                    do
                    {
                        SkipNewlines();
                        if (Check(TokenType.RightBracket)) break;
                        elements.Add(ParseExpression());
                    } while (Match(TokenType.Comma));
                }

                SkipNewlines();
                Consume(TokenType.RightBracket, "Expected ']' after list elements");
                return new ListExpr(elements);
            }

            if (Match(TokenType.LeftBrace))
            {
                var pairs = new Dictionary<Expression, Expression>();
                SkipNewlines();

                if (!Check(TokenType.RightBrace))
                {
                    do
                    {
                        SkipNewlines();
                        if (Check(TokenType.RightBrace)) break;

                        var key = ParseExpression();
                        Consume(TokenType.Colon, "Expected ':' in dictionary literal");
                        var value = ParseExpression();
                        pairs[key] = value;
                    } while (Match(TokenType.Comma));
                }

                SkipNewlines();
                Consume(TokenType.RightBrace, "Expected '}' after dictionary");
                return new DictExpr(pairs);
            }

            if (Match(TokenType.New))
            {
                var typeName = Consume(TokenType.Identifier, "Expected type name after 'new'").Value;

                if (Match(TokenType.LessThan))
                {
                    ParseTypeName();
                    Consume(TokenType.GreaterThan, "Expected '>' after generic type");
                }

                var args = new List<Expression>();
                if (Match(TokenType.LeftParen))
                {
                    if (!Check(TokenType.RightParen))
                    {
                        do
                        {
                            args.Add(ParseExpression());
                        } while (Match(TokenType.Comma));
                    }
                    Consume(TokenType.RightParen, "Expected ')' after constructor arguments");
                }

                return new NewExpr(typeName, args);
            }

            if (Match(TokenType.Identifier))
                return new IdentifierExpr(Previous.Value);

            // Type keywords in expression position: int(x) cast, etc.
            if (Current.Type >= TokenType.IntType && Current.Type <= TokenType.TaskType)
            {
                var typeName = ParseTypeName();

                if (Check(TokenType.LeftParen))
                {
                    Advance();
                    var innerExpr = ParseExpression();
                    Consume(TokenType.RightParen, "Expected ')' after cast");
                    return new AsExpr(innerExpr, typeName);
                }

                return new IdentifierExpr(typeName);
            }

            throw new ParseException(
                $"Line {Current.Line}: Unexpected token '{Current.Value}' ({Current.Type})");
        }

        // ---- lambdas & interpolated strings ----

        private LambdaExpr TryParseLambda()
        {
            int saved = _position;

            // single parameter without parens: x => expr
            if (Check(TokenType.Identifier) && Peek(1).Type == TokenType.FatArrow)
            {
                var lambda = new LambdaExpr();
                lambda.Parameters.Add(new Parameter(Advance().Value, "dynamic"));
                Advance(); // =>
                return FinishLambdaBody(lambda);
            }

            // (a, b) => ...  or  () => ...
            if (Check(TokenType.LeftParen))
            {
                // scan ahead: is there a FatArrow after the matching ')'?  If not,
                // this is an ordinary parenthesized expression — bail out early.
                int depth = 0;
                int scan = _position;
                while (scan < _tokens.Count)
                {
                    var t = _tokens[scan];
                    if (t.Type == TokenType.LeftParen) depth++;
                    else if (t.Type == TokenType.RightParen)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            var after = scan + 1 < _tokens.Count
                                ? _tokens[scan + 1].Type : TokenType.EOF;
                            if (after != TokenType.FatArrow) return null;
                            break;
                        }
                    }
                    else if (t.Type == TokenType.EOF) return null;
                    scan++;
                }

                try
                {
                    var lambda = new LambdaExpr();
                    Consume(TokenType.LeftParen, "Expected '('");
                    var shell = new FunctionDecl { Name = "lambda" };
                    ParseParameterList(shell, allowCSharpStyle: true);
                    Consume(TokenType.RightParen, "Expected ')' after lambda parameters");
                    foreach (var p in shell.Parameters)
                        lambda.Parameters.Add(p);
                    Consume(TokenType.FatArrow, "Expected '=>' after lambda parameters");
                    return FinishLambdaBody(lambda);
                }
                catch (ParseException)
                {
                    _position = saved;   // not a lambda — restore, parse as group
                    return null;
                }
            }

            return null;
        }

        private LambdaExpr FinishLambdaBody(LambdaExpr lambda)
        {
            SkipNewlines();
            if (Match(TokenType.LeftBrace))
            {
                lambda.Body.AddRange(ParseBlockStatements());
                Consume(TokenType.RightBrace, "Expected '}' to close lambda body");
            }
            else
            {
                lambda.IsExpressionBody = true;
                lambda.ExpressionBody = ParseExpression();
            }
            return lambda;
        }

        /// <summary>Builds an InterpStringExpr from the raw text inside $"...".
        /// Splits on braces (quote-aware, honors {{ }} escapes) and parses each
        /// hole as a full Nexus expression via a sub-lexer/parser.</summary>
        private Expression BuildInterpolatedString(string raw)
        {
            var result = new InterpStringExpr();
            var literal = new System.Text.StringBuilder();
            int i = 0;

            while (i < raw.Length)
            {
                char c = raw[i];

                if (c == '{' && i + 1 < raw.Length && raw[i + 1] == '{')
                { literal.Append('{'); i += 2; continue; }
                if (c == '}' && i + 1 < raw.Length && raw[i + 1] == '}')
                { literal.Append('}'); i += 2; continue; }

                if (c == '{')
                {
                    int depth = 1;
                    int j = i + 1;
                    while (j < raw.Length && depth > 0)
                    {
                        char d = raw[j];
                        if (d == '\"') { j = SkipQuoted(raw, j, '\"'); continue; }
                        if (d == '\'') { j = SkipQuoted(raw, j, '\''); continue; }
                        if (d == '{') depth++;
                        else if (d == '}') depth--;
                        j++;
                    }

                    if (depth != 0)
                        throw new ParseException("Unterminated '{' in interpolated string");

                    var inner = raw.Substring(i + 1, j - i - 2);
                    if (literal.Length > 0)
                    {
                        result.Parts.Add(literal.ToString());
                        literal.Clear();
                    }

                    var innerLexer = new Lexer(inner);
                    var innerTokens = innerLexer.Tokenize();
                    var innerParser = new Parser(innerTokens);
                    result.Parts.Add(innerParser.ParseExpression());
                    if (innerParser.Errors.Count > 0)
                        throw new ParseException(
                            $"Invalid expression in interpolated string: {innerParser.Errors[0]}");

                    i = j;
                    continue;
                }

                literal.Append(c);
                i++;
            }

            if (literal.Length > 0)
                result.Parts.Add(literal.ToString());
            return result;
        }

        private static int SkipQuoted(string s, int start, char quote)
        {
            int j = start + 1;
            while (j < s.Length)
            {
                if (s[j] == '\\') { j += 2; continue; }
                if (s[j] == quote) return j + 1;
                j++;
            }
            return j;
        }

        private static readonly HashSet<TokenType> MemberNameTokens = new HashSet<TokenType>
        {
            TokenType.Identifier,
            // keywords that are legal after '.' (get/set/... are reserved words
            // in statement position but ordinary member names here)
            TokenType.Get, TokenType.Set, TokenType.Operator, TokenType.Delegate,
            TokenType.Event, TokenType.Enum, TokenType.Print, TokenType.Println,
            TokenType.IntType, TokenType.FloatType, TokenType.DoubleType,
            TokenType.StringType, TokenType.BoolType, TokenType.ListType,
            TokenType.DictType,
        };

        private Token ConsumeMemberName(string message)
        {
            if (!IsAtEnd() && MemberNameTokens.Contains(Current.Type))
                return Advance();
            throw new ParseException(
                $"Line {Current.Line}: {message}. Got '{Current.Value}' ({Current.Type})");
        }

        private class ParseException : Exception
        {
            public ParseException(string message) : base(message) { }
        }
    }
}

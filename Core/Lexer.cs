using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.Core
{
    /// <summary>
    /// Lexical analyzer for Nexus source code.
    /// Guarantees forward progress: a stall guard forces an advance if any
    /// handler fails to consume input, so the lexer can never loop forever.
    /// </summary>
    public class Lexer
    {
        private readonly string _source;
        private int _position;
        private int _line = 1;
        private int _column = 1;
        private readonly List<Token> _tokens = new();
        private readonly List<string> _errors = new();

        public List<string> Errors => _errors;
        public int Line => _line;

        private static readonly Dictionary<string, TokenType> _interopAttributes = new()
        {
            ["DllImport"] = TokenType.DllImport,
            ["JavaImport"] = TokenType.JavaImport,
            ["NasmImport"] = TokenType.NasmImport,
            ["GoImport"] = TokenType.GoImport,
            ["CSharpImport"] = TokenType.CSharpImport,
            ["VbImport"] = TokenType.VbImport,
            ["FSharpImport"] = TokenType.FSharpImport,
            ["PythonImport"] = TokenType.PythonImport,
            ["RustImport"] = TokenType.RustImport,
            ["RubyImport"] = TokenType.RubyImport,
        };

        public Lexer(string source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public List<Token> Tokenize()
        {
            while (_position < _source.Length)
            {
                int before = _position;

                SkipWhitespaceAndComments();
                if (_position >= _source.Length) break;

                char c = _source[_position];

                try
                {
                    if (char.IsLetter(c) || c == '_')
                        ReadIdentifier();
                    else if (char.IsDigit(c))
                        ReadNumber();
                    else if (c == '"')
                        ReadString();
                    else if (c == '\'')
                        ReadChar();
                    else if (c == '@' && Peek(1) == '"')
                        ReadVerbatimString();
                    else
                        ReadSymbol();
                }
                catch (Exception ex)
                {
                    _errors.Add($"Line {_line}: {ex.Message}");
                    Advance();
                }

                // SAFETY NET: never allow a zero-progress iteration
                if (_position == before)
                {
                    _errors.Add($"Line {_line}: Lexer stalled on '{_source[_position]}' — forcing advance");
                    Advance();
                }
            }

            _tokens.Add(new Token(TokenType.EOF, "", _line, _column, _position));
            return _tokens;
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < _source.Length)
            {
                char c = _source[_position];

                if (c == ' ' || c == '\t' || c == '\r')
                {
                    Advance();
                }
                else if (c == '\n')
                {
                    if (_tokens.Count > 0 &&
                        _tokens[_tokens.Count - 1].Type != TokenType.Newline &&
                        _tokens[_tokens.Count - 1].Type != TokenType.Semicolon)
                    {
                        _tokens.Add(new Token(TokenType.Newline, "\\n", _line, _column, _position));
                    }
                    Advance();
                }
                else if (c == '/' && Peek(1) == '/')
                {
                    while (_position < _source.Length && _source[_position] != '\n')
                        Advance();
                }
                else if (c == '/' && Peek(1) == '*')
                {
                    Advance(); Advance();
                    int depth = 1;
                    while (_position < _source.Length && depth > 0)
                    {
                        if (_source[_position] == '*' && Peek(1) == '/')
                        {
                            depth--;
                            Advance(); Advance();
                        }
                        else if (_source[_position] == '/' && Peek(1) == '*')
                        {
                            depth++;
                            Advance(); Advance();
                        }
                        else if (_source[_position] == '\n')
                        {
                            _line++;
                            Advance();
                        }
                        else
                        {
                            Advance();
                        }
                    }
                }
                else
                {
                    break;
                }
            }
        }

        private void ReadIdentifier()
        {
            int start = _position;
            int startLine = _line;
            int startCol = _column;

            while (_position < _source.Length &&
                   (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
                Advance();

            string text = _source.Substring(start, _position - start);
            TokenType type = Keywords.IsKeyword(text)
                ? Keywords.GetKeywordType(text)
                : TokenType.Identifier;

            _tokens.Add(new Token(type, text, startLine, startCol, start));
        }

        private void ReadNumber()
        {
            int start = _position;
            int startLine = _line;
            int startCol = _column;
            bool isFloat = false;
            bool isHex = false;

            if (_source[_position] == '0' && _position + 1 < _source.Length &&
                (_source[_position + 1] == 'x' || _source[_position + 1] == 'X'))
            {
                isHex = true;
                Advance(); Advance();
                while (_position < _source.Length && IsHexDigit(_source[_position]))
                    Advance();
            }
            else
            {
                while (_position < _source.Length && char.IsDigit(_source[_position]))
                    Advance();

                if (_position < _source.Length && _source[_position] == '.' &&
                    _position + 1 < _source.Length && char.IsDigit(_source[_position + 1]))
                {
                    isFloat = true;
                    Advance();
                    while (_position < _source.Length && char.IsDigit(_source[_position]))
                        Advance();
                }

                // Scientific notation
                if (_position < _source.Length && (_source[_position] == 'e' || _source[_position] == 'E'))
                {
                    int save = _position;
                    Advance();
                    if (_position < _source.Length && (_source[_position] == '+' || _source[_position] == '-'))
                        Advance();
                    if (_position < _source.Length && char.IsDigit(_source[_position]))
                    {
                        isFloat = true;
                        while (_position < _source.Length && char.IsDigit(_source[_position]))
                            Advance();
                    }
                    else
                    {
                        _position = save;
                    }
                }
            }

            // Type suffix
            if (_position < _source.Length && "fFdDmMlLuU".Contains(_source[_position]))
            {
                if (char.ToLowerInvariant(_source[_position]) == 'f') isFloat = true;
                Advance();
            }

            string text = _source.Substring(start, _position - start);

            try
            {
                if (isHex)
                {
                    string hexPart = text.Substring(2);
                    _tokens.Add(new Token(TokenType.IntLiteral, text,
                        Convert.ToInt64(hexPart, 16), startLine, startCol, start));
                }
                else if (isFloat)
                {
                    _tokens.Add(new Token(TokenType.FloatLiteral, text,
                        double.Parse(text.TrimEnd('f', 'F', 'd', 'D', 'm', 'M')),
                        startLine, startCol, start));
                }
                else
                {
                    _tokens.Add(new Token(TokenType.IntLiteral, text,
                        long.Parse(text), startLine, startCol, start));
                }
            }
            catch
            {
                _errors.Add($"Line {startLine}: Invalid number format: {text}");
                _tokens.Add(new Token(TokenType.IntLiteral, "0", 0L, startLine, startCol, start));
            }
        }

        private void ReadString()
        {
            int startLine = _line;
            int startCol = _column;
            Advance(); // opening quote
            var sb = new StringBuilder();

            while (_position < _source.Length && _source[_position] != '"')
            {
                if (_source[_position] == '\\' && _position + 1 < _source.Length)
                {
                    Advance();
                    char escaped = _source[_position];
                    switch (escaped)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '0': sb.Append('\0'); break;
                        case 'a': sb.Append('\a'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'v': sb.Append('\v'); break;
                        default: sb.Append(escaped); break;
                    }
                    Advance();
                }
                else
                {
                    if (_source[_position] == '\n') _line++;
                    sb.Append(_source[_position]);
                    Advance();
                }
            }

            if (_position < _source.Length)
                Advance(); // closing quote
            else
                _errors.Add($"Line {startLine}: Unterminated string literal");

            string value = sb.ToString();
            _tokens.Add(new Token(TokenType.StringLiteral, value, value, startLine, startCol, startCol));
        }

        private void ReadVerbatimString()
        {
            int startLine = _line;
            int startCol = _column;
            Advance(); // @
            Advance(); // "
            var sb = new StringBuilder();

            while (_position < _source.Length)
            {
                if (_source[_position] == '"')
                {
                    if (Peek(1) == '"')
                    {
                        sb.Append('"');
                        Advance(); Advance();
                    }
                    else
                    {
                        Advance();
                        break;
                    }
                }
                else
                {
                    if (_source[_position] == '\n') _line++;
                    sb.Append(_source[_position]);
                    Advance();
                }
            }

            string value = sb.ToString();
            _tokens.Add(new Token(TokenType.StringLiteral, value, value, startLine, startCol, startCol));
        }

        private void ReadChar()
        {
            int startLine = _line;
            int startCol = _column;
            Advance(); // opening quote
            char c = '\0';

            if (_position < _source.Length)
            {
                if (_source[_position] == '\\' && _position + 1 < _source.Length)
                {
                    Advance();
                    switch (_source[_position])
                    {
                        case 'n': c = '\n'; break;
                        case 't': c = '\t'; break;
                        case 'r': c = '\r'; break;
                        case '\\': c = '\\'; break;
                        case '\'': c = '\''; break;
                        case '0': c = '\0'; break;
                        default: c = _source[_position]; break;
                    }
                }
                else
                {
                    c = _source[_position];
                }
                Advance();
            }

            if (_position < _source.Length && _source[_position] == '\'')
                Advance();
            else
                _errors.Add($"Line {startLine}: Unterminated character literal");

            _tokens.Add(new Token(TokenType.CharLiteral, c.ToString(), c, startLine, startCol, startCol));
        }

        private void ReadSymbol()
        {
            int startLine = _line;
            int startCol = _column;
            char c = _source[_position];

            // ---- Interop attributes: #[Name("...")] ----
            if (c == '#' && Peek(1) == '[')
            {
                Advance(); Advance(); // consume "#["

                var attrName = new StringBuilder();
                while (_position < _source.Length &&
                       (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
                {
                    attrName.Append(_source[_position]);
                    Advance();
                }

                string attr = attrName.ToString();
                if (attr.Length == 0)
                {
                    _errors.Add($"Line {startLine}: Malformed attribute");
                    return;
                }

                if (_interopAttributes.TryGetValue(attr, out TokenType attrType))
                    _tokens.Add(new Token(attrType, attr, startLine, startCol, startCol));
                else
                    _errors.Add($"Line {startLine}: Unknown attribute '{attr}'");

                // optional (args)
                if (_position < _source.Length && _source[_position] == '(')
                {
                    var argsBuilder = new StringBuilder();
                    int parenDepth = 0;
                    while (_position < _source.Length)
                    {
                        char ch = _source[_position];
                        if (ch == '(') parenDepth++;
                        if (ch == ')')
                        {
                            parenDepth--;
                            Advance();
                            if (parenDepth == 0) break;
                            continue;
                        }
                        argsBuilder.Append(ch);
                        Advance();
                    }

                    var argStr = argsBuilder.ToString();
                    // strip quotes for the simple single-argument form
                    if (argStr.Length >= 2 && argStr[0] == '"' && argStr[argStr.Length - 1] == '"')
                        argStr = argStr.Substring(1, argStr.Length - 2);

                    _tokens.Add(new Token(TokenType.StringLiteral, argStr, argStr,
                        startLine, startCol, startCol));
                }

                if (_position < _source.Length && _source[_position] == ']')
                    Advance();
                else
                    _errors.Add($"Line {startLine}: Expected ']' after attribute");
                return;
            }

            // ---- Directives: #name / #!... ----
            if (c == '#' && _position + 1 < _source.Length &&
                (char.IsLetter(_source[_position + 1]) || _source[_position + 1] == '!'))
            {
                Advance();
                var directive = new StringBuilder();
                while (_position < _source.Length && _source[_position] != '\n' && _source[_position] != ' ')
                {
                    directive.Append(_source[_position]);
                    Advance();
                }
                _tokens.Add(new Token(TokenType.Directive, directive.ToString(), startLine, startCol, startCol));
                return;
            }

            // ---- Operators & delimiters ----
            // Consume the first character unconditionally: every code path below
            // is guaranteed forward progress (the classic lexer infinite-loop bug).
            Advance();

            bool Match2(char second)   // consume a second char if it matches
            {
                if (_position < _source.Length && _source[_position] == second)
                {
                    Advance();
                    return true;
                }
                return false;
            }

            switch (c)
            {
                case '+': if (Match2('+')) AddToken(TokenType.PlusPlus, "++");
                          else if (Match2('=')) AddToken(TokenType.PlusAssign, "+=");
                          else AddToken(TokenType.Plus, "+"); break;

                case '-': if (Match2('>')) AddToken(TokenType.Arrow, "->");
                          else if (Match2('-')) AddToken(TokenType.MinusMinus, "--");
                          else if (Match2('=')) AddToken(TokenType.MinusAssign, "-=");
                          else AddToken(TokenType.Minus, "-"); break;

                case '*': if (Match2('=')) AddToken(TokenType.StarAssign, "*=");
                          else AddToken(TokenType.Star, "*"); break;

                case '/': if (Match2('=')) AddToken(TokenType.SlashAssign, "/=");
                          else AddToken(TokenType.Slash, "/"); break;

                case '%': if (Match2('=')) AddToken(TokenType.PercentAssign, "%=");
                          else AddToken(TokenType.Percent, "%"); break;

                case '=': if (Match2('=')) AddToken(TokenType.Equals, "==");
                          else if (Match2('>')) AddToken(TokenType.FatArrow, "=>");
                          else AddToken(TokenType.Assign, "="); break;

                case '!': if (Match2('=')) AddToken(TokenType.NotEquals, "!=");
                          else AddToken(TokenType.Not, "!"); break;

                case '<': if (Match2('<')) AddToken(TokenType.ShiftLeft, "<<");
                          else if (Match2('=')) AddToken(TokenType.LessEqual, "<=");
                          else AddToken(TokenType.LessThan, "<"); break;

                case '>': if (Match2('>')) AddToken(TokenType.ShiftRight, ">>");
                          else if (Match2('=')) AddToken(TokenType.GreaterEqual, ">=");
                          else AddToken(TokenType.GreaterThan, ">"); break;

                case '&': if (Match2('&')) AddToken(TokenType.AndAnd, "&&");
                          else AddToken(TokenType.BitAnd, "&"); break;

                case '|': if (Match2('|')) AddToken(TokenType.OrOr, "||");
                          else AddToken(TokenType.BitOr, "|"); break;

                case '^': AddToken(TokenType.BitXor, "^"); break;
                case '~': AddToken(TokenType.BitNot, "~"); break;

                case '?': if (Match2('?')) AddToken(TokenType.QuestionQuestion, "??");
                          else AddToken(TokenType.Question, "?"); break;

                case ':': AddToken(TokenType.Colon, ":"); break;
                case ';': AddToken(TokenType.Semicolon, ";"); break;
                case '(': AddToken(TokenType.LeftParen, "("); break;
                case ')': AddToken(TokenType.RightParen, ")"); break;
                case '{': AddToken(TokenType.LeftBrace, "{"); break;
                case '}': AddToken(TokenType.RightBrace, "}"); break;
                case '[': AddToken(TokenType.LeftBracket, "["); break;
                case ']': AddToken(TokenType.RightBracket, "]"); break;

                case '.':
                    if (_position + 1 < _source.Length &&
                        _source[_position] == '.' && _source[_position + 1] == '.')
                    {
                        Advance(); Advance();
                        AddToken(TokenType.Dot, "...");
                    }
                    else if (_position < _source.Length && _source[_position] == '.')
                    {
                        Advance();
                        AddToken(TokenType.DotDot, "..");   // range operator: 1..5
                    }
                    else AddToken(TokenType.Dot, ".");
                    break;

                case ',': AddToken(TokenType.Comma, ","); break;
                case '$': AddToken(TokenType.Dollar, "$"); break;

                default:
                    _errors.Add($"Line {startLine}: Unexpected character '{c}'");
                    break; // c already consumed — do NOT advance again
            }
        }

        private void AddToken(TokenType type, string value)
        {
            _tokens.Add(new Token(type, value, _line, _column, _position));
        }

        private char Peek(int offset = 1)
        {
            int pos = _position + offset;
            return pos < _source.Length ? _source[pos] : '\0';
        }

        private void Advance()
        {
            if (_position < _source.Length)
            {
                if (_source[_position] == '\n')
                {
                    _line++;
                    _column = 1;
                }
                else
                {
                    _column++;
                }
                _position++;
            }
        }

        private static bool IsHexDigit(char c) =>
            char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}

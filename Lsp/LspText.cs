using System;
using System.Collections.Generic;
using Nexus.Core;

namespace Nexus.Lsp
{
    /// <summary>Text/position helpers bridging LSP coordinates (0-based,
    /// UTF-16 chars) and Nexus lexer coordinates (1-based line/column).</summary>
    public static class LspText
    {
        /// <summary>Convert an LSP (line, character) to an absolute offset. Clamps.</summary>
        public static int OffsetAt(string text, int line, int character)
        {
            if (text == null) return 0;
            if (line < 0) line = 0;
            int currentLine = 0, offset = 0;
            while (offset < text.Length && currentLine < line)
            {
                if (text[offset] == '\n') currentLine++;
                offset++;
            }
            // consume `character` code units, but never cross the line end
            int col = 0;
            while (offset < text.Length && col < character && text[offset] != '\n')
            {
                offset++;
                col++;
            }
            return offset;
        }

        /// <summary>Convert an absolute offset to LSP (line, character).</summary>
        public static (int line, int character) PositionAt(string text, int offset)
        {
            if (text == null) return (0, 0);
            if (offset < 0) offset = 0;
            if (offset > text.Length) offset = text.Length;

            int line = 0, lineStart = 0;
            for (int i = 0; i < offset; i++)
            {
                if (text[i] == '\n') { line++; lineStart = i + 1; }
            }
            return (line, offset - lineStart);
        }

        /// <summary>The identifier-shaped word ending exactly at (or containing) offset.</summary>
        public static string WordBefore(string text, int offset)
        {
            if (text == null || offset <= 0) return "";
            int end = Math.Min(offset, text.Length);
            int start = end;
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
                start--;
            return start < end ? text.Substring(start, end - start) : "";
        }

        /// <summary>Identifier-shaped word around offset (used for hover/definition).</summary>
        public static string WordAt(string text, int offset)
        {
            if (text == null || offset < 0 || offset >= text.Length) return WordBefore(text, Math.Max(0, (text?.Length ?? 0)));
            if (!IsWordChar(text[offset]))
            {
                // between words: prefer the word just before the caret
                return WordBefore(text, offset);
            }
            int start = offset, end = offset;
            while (start > 0 && IsWordChar(text[start - 1])) start--;
            while (end < text.Length && IsWordChar(text[end])) end++;
            return text.Substring(start, end - start);
        }

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Crude comment detection: scans from the start with a tiny state
        /// machine (strings skipped, line + block comments tracked).</summary>
        public static bool IsInComment(string text, int offset)
        {
            if (string.IsNullOrEmpty(text) || offset <= 0) return false;
            int limit = Math.Min(offset, text.Length);
            int i = 0;
            bool inBlock = false;
            while (i < limit)
            {
                if (inBlock)
                {
                    if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/') { inBlock = false; i += 2; }
                    else i++;
                }
                else if (text[i] == '"')
                {
                    i++;
                    while (i < limit && text[i] != '"')
                    {
                        if (text[i] == '\\') i++;
                        i++;
                    }
                    i++;   // closing quote (or past limit while typing)
                }
                else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    inBlock = true;
                    i += 2;
                }
                else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    int j = i;
                    while (j < text.Length && text[j] != '\n') j++;
                    if (offset > i && offset <= j) return true;
                    i = j;
                }
                else i++;
            }
            return inBlock;
        }

        /// <summary>True when offset sits inside a string literal token.</summary>
        public static bool IsInString(List<Token> tokens, int offset)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Type == TokenType.EOF) break;
                int start = t.Position;
                int end = t.Position + RawLength(t);
                if (offset > start && offset < end && t.Type == TokenType.StringLiteral)
                    return true;
                if (start > offset) break;
            }
            return false;
        }

        /// <summary>Approximate raw source length of a token's lexeme.</summary>
        public static int RawLength(Token t)
        {
            if (t.Type == TokenType.StringLiteral) return t.Value.Length + 2;
            if (t.Type == TokenType.CharLiteral) return Math.Max(t.Value.Length, 1) + 2;
            if (t.Type == TokenType.Newline) return 1;
            return t.Value?.Length ?? 0;
        }

        /// <summary>Index of the last token starting at or before offset (EOF excluded).</summary>
        public static int TokenIndexAt(List<Token> tokens, int offset)
        {
            int best = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Type == TokenType.EOF) break;
                if (t.Position <= offset) best = i; else break;
            }
            return best;
        }

        /// <summary>True when the innermost unclosed `{` belongs to an `asm` block.</summary>
        public static bool IsInsideAsm(List<Token> tokens, int offset)
        {
            var stack = new Stack<bool>();      // true = asm block
            bool asmNext = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Type == TokenType.EOF || t.Position > offset) break;

                if (t.Type == TokenType.Asm) { asmNext = true; continue; }

                if (t.Type == TokenType.LeftBrace)
                {
                    stack.Push(asmNext);
                    asmNext = false;
                }
                else if (t.Type == TokenType.RightBrace)
                {
                    if (stack.Count > 0) stack.Pop();
                    asmNext = false;
                }
                else if (t.Type != TokenType.Newline && asmNext)
                {
                    // `asm` not followed by `{` (inline asm statement) — cancel
                    asmNext = false;
                }
            }
            return stack.Count > 0 && stack.Peek();
        }
    }
}

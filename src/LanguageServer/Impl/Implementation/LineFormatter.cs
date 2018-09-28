﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.PythonTools;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Analysis.Infrastructure;
using Microsoft.PythonTools.Parsing;

namespace Microsoft.Python.LanguageServer.Implementation {
    /// <summary>
    /// LineFormatter formats lines of code to generally conform with PEP8.
    /// </summary>
    public class LineFormatter {
        private static readonly TextEdit[] NoEdits = Array.Empty<TextEdit>();

        private readonly TokenizerWrapper _tokenizer;
        private readonly Dictionary<int, List<TokenExt>> _lineTokens;

        /// <summary>
        /// Creates a LineFormatter from a reader. It will only read as much
        /// of the input as is needed to format the requested line.
        /// </summary>
        /// <param name="reader">The code to be formatted. LineFormatter does not dispose of the reader.</param>
        /// <param name="languageVersion">Language version to use in the tokenization format.</param>
        public LineFormatter(TextReader reader, PythonLanguageVersion languageVersion) {
            var tokenizer = new Tokenizer(languageVersion, options: TokenizerOptions.Verbatim | TokenizerOptions.VerbatimCommentsAndLineJoins | TokenizerOptions.GroupingRecovery);
            tokenizer.Initialize(reader);
            _tokenizer = new TokenizerWrapper(tokenizer);
            _lineTokens = new Dictionary<int, List<TokenExt>>();
        }

        private void AddToken(TokenExt token) {
            var line = token.Line;

            // Explicit line joins ("\") appear at the end of a line, but
            // their span ends on another line, so move backward so they can
            // be inserted in the right place.
            if (token.Kind == TokenKind.ExplicitLineJoin) {
                line--;
            }

            if (!_lineTokens.TryGetValue(line, out List<TokenExt> tokens)) {
                tokens = new List<TokenExt>();
                _lineTokens.Add(line, tokens);
            }

            tokens.Add(token);
        }

        /// <summary>
        /// Tokenizes up to and including the specified line. Tokens are
        /// stored in _lineTokens. If the provided line number is past the
        /// end of the input text, then the tokenizer will stop.
        /// Additionally, this function will attempt to read ahead onto the
        /// next line to the first non-ignored token so that the formatter
        /// can look ahead.
        /// </summary>
        /// <param name="line">One-indexed line number.</param>
        /// <param name="includeToken">A function which returns true if the token should be added to the final list. If null, all tokens will be added.</param>
        /// <returns>A non-null list of tokens on that line.</returns>
        private List<TokenExt> TokenizeLine(int line, Func<TokenExt, bool> includeToken = null) {
            Check.Argument(nameof(line), () => line > 0);

            var extraToken = true;

            var peeked = _tokenizer.Peek();
            while (peeked != null && (peeked.Line <= line || extraToken)) {
                var token = _tokenizer.Next();

                if (includeToken == null || includeToken(token)) {
                    AddToken(token);
                }

                peeked = _tokenizer.Peek();

                if (token.Line > line && !token.IsIgnored) {
                    extraToken = false;
                }
            }

            if (!_lineTokens.TryGetValue(line, out List<TokenExt> tokens)) {
                return new List<TokenExt>();
            }

            return tokens;
        }

        /// <summary>
        /// Formats a single line and returns TextEdits to replace the old text.
        /// </summary>
        /// <param name="line">One-indexed line number.</param>
        /// <returns>A list of TextEdits needed to format the line.</returns>
        public TextEdit[] FormatLine(int line) {
            if (line < 1) {
                return NoEdits;
            }

            // Keep ExplictLineJoin because it has text associated with it.
            var tokens = TokenizeLine(line, t => !t.IsIgnored || t.Kind == TokenKind.ExplicitLineJoin);

            if (tokens.Count == 0) {
                return NoEdits;
            }

            var builder = new StringBuilder();
            var first = tokens[0];
            var beginCol = first.Span.Start.Column;
            var startIdx = 0;

            if (first.IsMultilineString) {
                // If the first token is a multiline string, start the edit afterward,
                // skip looking at the first token, and ensure that there's a space
                // after it if needed (i.e. in the case of a following comment).
                beginCol = first.Span.End.Column;
                startIdx = 1;
                if (builder.Length == 0) {
                    builder.Append(' ');
                } else { 
                    builder.EnsureEndsWithWhiteSpace();
                }
            }
            
            for (var i = startIdx; i < tokens.Count; i++) {
                var token = tokens[i];
                var prev = tokens.ElementAtOrDefault(i - 1);
                var next = tokens.ElementAtOrDefault(i + 1);

                switch (token.Kind) {
                    case TokenKind.Comment:
                        builder.EnsureEndsWithWhiteSpace(2);
                        builder.Append(token);
                        break;

                    case TokenKind.Assign when token.IsInsideFunctionArgs:
                        // Search backwards through the tokens looking for a colon for this argument,
                        // indicating that there's a type hint and spacing should surround the equals.
                        for (var p = token.PrevNonIgnored; p != null; p = p.PrevNonIgnored) {
                            if (p == token.Inside) {
                                // Hit the surrounding left parenthesis, so stop the search.
                                builder.Append(token);
                                break;
                            }

                            if (p.Inside != token.Inside) {
                                // Inside another grouping than the =, so skip over it.
                                continue;
                            }

                            if (p.Kind == TokenKind.Comma) {
                                // Found a comma, indicating the end of another argument, so stop.
                                builder.Append(token);
                                break;
                            }

                            if (p.Kind == TokenKind.Colon) {
                                // Found a colon before hitting another argument or the opening parenthesis, so add spacing.
                                AppendTokenEnsureWhiteSpacesAround(builder, token);
                                break;
                            }
                        }
                        break;

                    case TokenKind.Assign:
                        AppendTokenEnsureWhiteSpacesAround(builder, token);
                        break;

                    // "Normal" assignment and function parameters with type hints
                    case TokenKind.AddEqual:
                    case TokenKind.SubtractEqual:
                    case TokenKind.PowerEqual:
                    case TokenKind.MultiplyEqual:
                    case TokenKind.MatMultiplyEqual:
                    case TokenKind.FloorDivideEqual:
                    case TokenKind.DivideEqual:
                    case TokenKind.ModEqual:
                    case TokenKind.LeftShiftEqual:
                    case TokenKind.RightShiftEqual:
                    case TokenKind.BitwiseAndEqual:
                    case TokenKind.BitwiseOrEqual:
                    case TokenKind.ExclusiveOrEqual:
                        AppendTokenEnsureWhiteSpacesAround(builder, token);
                        break;

                    case TokenKind.Comma:
                        builder.Append(token);
                        if (next != null && !next.IsClose && next.Kind != TokenKind.Colon) {
                            builder.EnsureEndsWithWhiteSpace();
                        }
                        break;

                    // Slicing
                    case TokenKind.Colon when token.Inside?.Kind == TokenKind.LeftBracket:
                        if (!token.IsSimpleSliceToLeft) {
                            builder.EnsureEndsWithWhiteSpace();
                        }

                        builder.Append(token);

                        if (!token.IsSimpleSliceToRight) {
                            builder.EnsureEndsWithWhiteSpace();
                        }

                        break;

                    case TokenKind.Colon:
                        builder.Append(token);
                        if (next != null && !next.IsColonOrComma) {
                            builder.EnsureEndsWithWhiteSpace();
                        }
                        break;

                    case TokenKind.At:
                        if (prev != null) {
                            AppendTokenEnsureWhiteSpacesAround(builder, token);
                        } else {
                            builder.Append(token);
                        }
                        break;

                    // Unary
                    case TokenKind.Add:
                    case TokenKind.Subtract:
                    case TokenKind.Twiddle:
                        if (prev != null && (prev.IsOperator || prev.IsOpen || prev.IsColonOrComma)) {
                            builder.Append(token);
                        } else {
                            AppendTokenEnsureWhiteSpacesAround(builder, token);
                        }
                        break;

                    case TokenKind.Power:
                    case TokenKind.Multiply:
                    {
                        var actualPrev = token.PrevNonIgnored;
                        if (token.Inside != null && actualPrev != null && (actualPrev.Kind == TokenKind.Comma || actualPrev.IsOpen || token.Inside.Kind == TokenKind.KeywordLambda)) {
                            builder.Append(token);
                            // Check unpacking case
                        } else if (token.Kind == TokenKind.Multiply && (actualPrev == null || actualPrev.Kind != TokenKind.Name && actualPrev.Kind != TokenKind.Constant && !actualPrev.IsClose)) {
                            builder.Append(token);
                        } else { 
                            AppendTokenEnsureWhiteSpacesAround(builder, token);
                        }
                        break;
                    }

                    // Operators
                    case TokenKind.MatMultiply:
                    case TokenKind.FloorDivide:
                    case TokenKind.Divide:
                    case TokenKind.Mod:
                    case TokenKind.LeftShift:
                    case TokenKind.RightShift:
                    case TokenKind.BitwiseAnd:
                    case TokenKind.BitwiseOr:
                    case TokenKind.ExclusiveOr:
                    case TokenKind.LessThan:
                    case TokenKind.GreaterThan:
                    case TokenKind.LessThanOrEqual:
                    case TokenKind.GreaterThanOrEqual:
                    case TokenKind.Equals:
                    case TokenKind.NotEquals:
                    case TokenKind.LessThanGreaterThan:
                    case TokenKind.Arrow:
                        AppendTokenEnsureWhiteSpacesAround(builder, token);
                        break;

                    case TokenKind.Dot:
                        if (prev != null && (prev.Kind == TokenKind.KeywordFrom || prev.IsNumber)) {
                            builder.EnsureEndsWithWhiteSpace();
                        }

                        builder.Append(token);
                        break;

                    case TokenKind.LeftBrace:
                    case TokenKind.LeftBracket:
                    case TokenKind.LeftParenthesis:
                    case TokenKind.RightBrace:
                    case TokenKind.RightBracket:
                    case TokenKind.RightParenthesis:
                        builder.Append(token);
                        break;

                    case TokenKind.Semicolon:
                        builder.Append(token);
                        builder.EnsureEndsWithWhiteSpace();
                        break;

                    case TokenKind.Constant when next != null && next.IsString:
                        builder.Append(token);
                        builder.EnsureEndsWithWhiteSpace();
                        break;

                    case TokenKind.Constant:
                        builder.Append(token);
                        break;

                    case TokenKind.Name:
                    case TokenKind.KeywordFalse:
                    case TokenKind.KeywordTrue:
                    case TokenKind.Ellipsis: // Ellipsis is a value
                        builder.Append(token);
                        break;

                    case TokenKind.ExplicitLineJoin:
                        builder.EnsureEndsWithWhiteSpace();
                        builder.Append("\\"); // Hardcoded string so that any following whitespace doesn't make it in.
                        break;

                    case TokenKind.BackQuote:
                        builder.Append(token);
                        break;

                    case TokenKind.KeywordLambda when token.IsInsideFunctionArgs && prev?.Kind == TokenKind.Assign:
                        builder.Append(token);

                        if (next?.Kind != TokenKind.Colon) {
                            builder.EnsureEndsWithWhiteSpace();
                        }

                        break;

                    default:
                        if (token.IsKeyword) {
                            if (prev != null && !prev.IsOpen) {
                                builder.EnsureEndsWithWhiteSpace();
                            }

                            builder.Append(token);

                            if (next != null && next.Kind != TokenKind.Colon && next.Kind != TokenKind.Semicolon) {
                                builder.EnsureEndsWithWhiteSpace();
                            }
                        } else { 
                            // No tokens should make it to this case, but try to keep things separated.
                            AppendTokenEnsureWhiteSpacesAround(builder, token);
                        }
                        break;
                }
            }

            var endCol = _tokenizer.EndOfLineCol(line);

            var afterLast = tokens.Last().Next;
            if (afterLast != null && afterLast.IsMultilineString) {
                // If the the next token is a multiline string, then make
                // sure to include that string's prefix on this line.
                var afterLastFirst = SplitByNewline(afterLast.ToString()).First();
                builder.Append(afterLastFirst);
            }

            builder.TrimEnd();
            var newText = builder.ToString();

            if (newText.Length == 0) {
                return NoEdits;
            }

            var edit = new TextEdit {
                range = new Range {
                    start = new SourceLocation(line, beginCol),
                    end = new SourceLocation(line, endCol)
                },
                newText = newText
            };

            return new[] { edit };
        }

        private static void AppendTokenEnsureWhiteSpacesAround(StringBuilder builder, TokenExt token) 
            => builder.EnsureEndsWithWhiteSpace()
            .Append(token)
            .EnsureEndsWithWhiteSpace();

        private class TokenExt {
            public Token Token { get; set; }
            public SourceSpan Span { get; set; }
            public int Line => Span.End.Line;
            public TokenExt Inside { get; set; }
            public TokenExt Prev { get; set; }
            public TokenExt Next { get; set; }
            public string PrecedingWhitespace { get; set; }
            public TokenKind Kind => Token.Kind;

            public override string ToString() => Token.VerbatimImage;

            public bool IsIgnored => Is(TokenKind.NewLine, TokenKind.NLToken, TokenKind.Indent, TokenKind.Dedent, TokenKind.ExplicitLineJoin);

            public bool IsOpen => Is(TokenKind.LeftBrace, TokenKind.LeftBracket, TokenKind.LeftParenthesis);

            public bool IsClose => Is(TokenKind.RightBrace, TokenKind.RightBracket, TokenKind.RightParenthesis);

            public bool IsColonOrComma => Is(TokenKind.Colon, TokenKind.Comma);

            public bool MatchesClose(TokenExt other) {
                switch (Kind) {
                    case TokenKind.LeftBrace:
                        return other.Kind == TokenKind.RightBrace;
                    case TokenKind.LeftBracket:
                        return other.Kind == TokenKind.RightBracket;
                    case TokenKind.LeftParenthesis:
                        return other.Kind == TokenKind.RightParenthesis;
                }

                return false;
            }

            public bool IsOperator => Token is OperatorToken || Is(TokenKind.Dot, TokenKind.Assign, TokenKind.Twiddle);

            public bool IsUnaryOp => Is(TokenKind.Add, TokenKind.Subtract, TokenKind.Twiddle);

            public bool IsInsideFunctionArgs => (Inside?.Kind == TokenKind.LeftParenthesis && Inside.PrevNonIgnored?.Kind == TokenKind.Name) || (Inside?.Kind == TokenKind.KeywordLambda);

            public bool IsNumber => Kind == TokenKind.Constant && Token != Tokens.NoneToken && !(Token.Value is string || Token.Value is AsciiString);

            public bool IsKeyword => (Kind >= TokenKind.FirstKeyword && Kind <= TokenKind.LastKeyword) || Kind == TokenKind.KeywordAsync || Kind == TokenKind.KeywordAwait;

            public bool IsString => Kind == TokenKind.Constant && Token != Tokens.NoneToken && (Token.Value is string || Token.Value is AsciiString);

            public bool IsMultilineString => Span.Start.Line != Span.End.Line && IsString;

            public bool IsSimpleSliceToLeft {
                get {
                    if (Kind != TokenKind.Colon) {
                        return false;
                    }

                    var a = PrevNonIgnored;
                    var b = a?.PrevNonIgnored;
                    var c = b?.PrevNonIgnored;

                    if (a == null) {
                        return false;
                    }

                    if (a.Is(TokenKind.LeftBracket, TokenKind.Colon)) {
                        return true;
                    }

                    if ((!a.IsNumber && a.Kind != TokenKind.Name) || b == null) {
                        return false;
                    }

                    if (b.Is(TokenKind.LeftBracket, TokenKind.Colon, TokenKind.Comma)) {
                        return true;
                    }

                    if (!b.IsUnaryOp || c == null) {
                        return false;
                    }

                    return c.Is(TokenKind.LeftBracket, TokenKind.Colon, TokenKind.Comma);
                }
            }

            public bool IsSimpleSliceToRight {
                get {
                    if (Kind != TokenKind.Colon) {
                        return false;
                    }

                    var a = NextNonIgnored;
                    var b = a?.NextNonIgnored;
                    var c = b?.NextNonIgnored;

                    if (a == null) {
                        return false;
                    }

                    if (a.Is(TokenKind.RightBracket, TokenKind.Colon, TokenKind.Comma)) {
                        return true;
                    }

                    if (b == null) {
                        return false;
                    }

                    if (a.IsUnaryOp) {
                        if (c == null) {
                            return false;
                        }
                        return (b.IsNumber || b.Kind == TokenKind.Name) && c.Is(TokenKind.RightBracket, TokenKind.Colon, TokenKind.Comma);
                    }

                    return (a.IsNumber || a.Kind == TokenKind.Name) && b.Is(TokenKind.RightBracket, TokenKind.Colon, TokenKind.Comma);
                }
            }

            public TokenExt PrevNonIgnored {
                get {
                    if (Prev != null) {
                        if (Prev.IsIgnored) {
                            return Prev.PrevNonIgnored;
                        }
                        return Prev;
                    }
                    return null;
                }
            }

            public TokenExt NextNonIgnored {
                get {
                    if (Next != null) {
                        if (Next.IsIgnored) {
                            return Next.NextNonIgnored;
                        }
                        return Next;
                    }
                    return null;
                }
            }

            private bool Is(params TokenKind[] kinds) => kinds.Contains(Kind);
        }

        /// <summary>
        /// TokenizerWrapper wraps a tokenizer, producing a stream of TokenExt
        /// instead of regular Tokens. The wrapper keeps track of brackets and
        /// lambdas, and allows peeking forward at the next token without
        /// advancing the tokenizer.
        /// </summary>
        private class TokenizerWrapper {
            private readonly Tokenizer _tokenizer;
            private readonly Stack<TokenExt> _insides = new Stack<TokenExt>();
            private TokenExt _peeked = null;
            private TokenExt _prev = null;

            public TokenizerWrapper(Tokenizer tokenizer) {
                _tokenizer = tokenizer;
            }

            /// <summary>
            /// Returns the next token, and advances the tokenizer. Note that
            /// the returned token's Next will not be set until the tokenizer
            /// actually reads that next token.
            /// </summary>
            /// <returns>The next token</returns>
            public TokenExt Next() {
                if (_peeked != null) {
                    var tmp = _peeked;
                    _peeked = null;
                    return tmp;
                }

                if (_tokenizer.IsEndOfFile) {
                    return null;
                }

                var token = _tokenizer.GetNextToken();

                if (token.Kind == TokenKind.EndOfFile) {
                    return null;
                }

                var tokenSpan = _tokenizer.TokenSpan;
                var sourceSpan = new SourceSpan(_tokenizer.IndexToLocation(tokenSpan.Start), _tokenizer.IndexToLocation(tokenSpan.End));

                var tokenExt = new TokenExt {
                    Token = token,
                    PrecedingWhitespace = _tokenizer.PreceedingWhiteSpace,
                    Span = sourceSpan,
                    Prev = _prev
                };

                if (tokenExt.IsClose) {
                    if (_insides.Count == 0 || !_insides.Peek().MatchesClose(tokenExt)) {
                        throw new Exception($"Close bracket ({token.Kind}) has no matching open");
                    }
                    _insides.Pop();
                } else if (tokenExt.Kind == TokenKind.Colon && _insides.Count != 0 && _insides.Peek().Kind == TokenKind.KeywordLambda) {
                    _insides.Pop();
                }

                if (_insides.TryPeek(out TokenExt inside)) {
                    tokenExt.Inside = inside;
                }

                if (tokenExt.IsOpen || tokenExt.Kind == TokenKind.KeywordLambda) {
                    _insides.Push(tokenExt);
                }

                if (_prev != null) {
                    _prev.Next = tokenExt;
                }

                _prev = tokenExt;
                return tokenExt;
            }

            /// <summary>
            /// Returns the next token without advancing the tokenizer. Note that
            /// the returned token's Next will not be set until the tokenizer
            /// actually reads that next token.
            /// </summary>
            /// <returns>The next token</returns>
            public TokenExt Peek() {
                if (_peeked != null) {
                    return _peeked;
                }

                _peeked = Next();
                return _peeked;
            }

            /// <summary>
            /// Gets the one-indexed column number of the end of a line. The
            /// tokenizer must be past the line's newline (or at EOF) in order
            /// for this function to work.
            /// </summary>
            /// <param name="line">A one-indexed line number.</param>
            /// <returns>One-indexed column number for the end of the line</returns>
            public int EndOfLineCol(int line) {
                if (line > _tokenizer.CurrentPosition.Line || (line == _tokenizer.CurrentPosition.Line && !_tokenizer.IsEndOfFile)) {
                    throw new ArgumentException("tokenizer must be at EOF or past line's newline", nameof(line));
                }

                var idx = line - 1;
                var lines = _tokenizer.GetLineLocations();

                if (idx < lines.Length) {
                    var nlLoc = lines[idx];

                    var sourceLocation = _tokenizer.IndexToLocation(nlLoc.EndIndex - 1);
                    return sourceLocation.Column;
                }

                return _tokenizer.CurrentPosition.Column;
            }
        }

        private static string[] SplitByNewline(string s) => s.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
    }
}

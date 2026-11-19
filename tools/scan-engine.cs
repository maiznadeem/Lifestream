// Credential scanner engine for tools/scan-artifacts.ps1 (compiled there with Add-Type; nothing else builds or ships it).
//
// Two modes, chosen per piece of content:
//   Binary mode  every file that is not plain text, and EVERY member of an archive (zip/nupkg/msix, tar, gzip, an installer
//                with an appended zip). There is NO test on the byte before a keyword: in compiled .NET code that byte is a
//                string length prefix and can be any value. A keyword (password, pwd, user id, uid, accountkey, client_secret ...)
//                anywhere, in UTF-8, UTF-16LE and UTF-16BE at both byte alignments, followed by "=" or ":" and a value, is a hit
//                when the value is not a placeholder and either a connection-string key (server=, database=, uid= ...) is within
//                300 bytes or the value has at least 6 characters.
//   Text mode    plain-text files on disk (configs, logs, source) and every string read out of .NET metadata. The keyword must be
//                a whole word (not after an ASCII letter, digit, _ $ @ % . -) and any non-placeholder value is a hit.
// .NET assemblies are also read with System.Reflection.Metadata: user strings (#US), custom-attribute blobs, string constants,
// manifest resources (.resources strings, BAML and other streams), and the text rules run on every string found there.
// Known benign strings in third-party files are accepted only through the reviewed binary allow-list (file name + rule +
// SHA-256 of the matched text); source lines only through the source allow-list (tests/ path + rule + SHA-256 of the line).
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LifestreamScan
{
    public sealed class Finding
    {
        public string File { get; set; }
        public string Rule { get; set; }
        public string Encoding { get; set; }
        public long Offset { get; set; }
        public string MatchSha256 { get; set; }
        public int Line { get; set; }
        public string LineSha256 { get; set; }
    }

    public sealed class Engine
    {
        public int MaxArchiveDepth = 6;
        public int MaxFindingsPerFile = 20;
        public long MaxEntryBytes = 512L * 1024 * 1024;
        // "path|rule|lineSha256", path relative to the repository root (source files only).
        public readonly HashSet<string> SourceAllow = new HashSet<string>(StringComparer.Ordinal);
        // "file name (lower case)|rule|matchSha256" (everything that is not a source file).
        public readonly HashSet<string> BinaryAllow = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<Finding> Findings = new List<Finding>();
        public readonly List<string> Errors = new List<string>();
        public long FilesScanned;
        public long BytesScanned;
        public int AllowListed;
        // The allow-list entries that matched something in this scan (so a test can find entries that no longer match anything).
        public readonly HashSet<string> AllowUsed = new HashSet<string>(StringComparer.Ordinal);
        public int MetadataErrors;
        public int FindingsOverCap;

        const int ChunkSize = 4 * 1024 * 1024;
        const int Overlap = 4096;          // > longest keyword + 256-char value in UTF-16 (2 bytes) or UTF-8 (4 bytes) + 300-byte context
        const int ContextBytes = 300;
        const int MaxValueChars = 256;

        static readonly RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        static readonly RegexOptions Cs = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        const string Boundary = @"(?<![A-Za-z0-9_$@%.\-])";

        // Binary mode: keyword anywhere, "=" or ":" (not "==", "=>", "::", "=~", ":="), optional quote before the separator (JSON).
        static readonly Regex BinaryKeyword = new Regex(
            @"(?<kw>password|passwd|pwd|user[ \t]*id|uid|accountkey|sharedaccesskey|client_?secret)[""']?[ \t]*[=:](?![=:>~])[ \t]*", Ci);
        // Connection-string keys. A user id next to another user id is not a connection string, so for the user-id keywords only the
        // other keys count (group "id" is the user-id keys).
        static readonly Regex ContextKey = new Regex(
            @"(?:(?<id>user[ \t]*id|uid)|server|data[ \t]*source|initial[ \t]*catalog|database|address|addr)[""']?[ \t]*[=:](?![=:>~])", Ci);

        sealed class TextRule
        {
            public string Name;
            public Regex Rx;
            public bool SourceOnly;
            public bool Keyword = true;   // false: the whole match is the hit (JWT, PEM, URL credentials)
        }

        static readonly TextRule[] TextRules =
        {
            new TextRule { Name = "PasswordValue", Rx = new Regex(Boundary + @"(?:password|pwd)\s*=(?![=>~])\s*",Ci) },
            // Prefixed keys (DbPassword=, MSSQL_SA_PASSWORD=, AdminPwd=) in the tight key=value form only, so C# "NewPassword = x;" is code.
            new TextRule { Name = "PasswordValue", Rx = new Regex(Boundary + @"(?:[A-Za-z0-9_]+password|(?:db|sql|sa|admin|app)_?pwd)=(?![=>~])", Ci) },
            // Source files only: a quoted literal assigned to a member (vm.Password = "x").
            new TextRule { Name = "PasswordValue", SourceOnly = true, Rx = new Regex(@"(?<=[A-Za-z0-9_\])]\.)(?:password|pwd)\s*=(?![=>~])\s*(?="")", Ci) },
            // "User ID" (any case, with a space) and the exact synonym "UserID"; identifier-style userId is code.
            new TextRule { Name = "UserIdValue", Rx = new Regex(Boundary + @"(?:(?i:user\s+id)|UserID)\s*=(?![=>~])\s*",Cs) },
            new TextRule { Name = "SecretValue", Rx = new Regex(Boundary + @"(?:accountkey|sharedaccesskey|client_?secret)\s*=(?![=>~])\s*",Ci) },
            // JSON: "Password": "x", "client_secret": "x"
            new TextRule { Name = "PasswordValue", Rx = new Regex(@"""(?:password|passwd|pwd|[A-Za-z0-9_]+password)""\s*:\s*(?="")", Ci) },
            new TextRule { Name = "SecretValue", Rx = new Regex(@"""(?:accountkey|sharedaccesskey|client_?secret)""\s*:\s*(?="")", Ci) },
            new TextRule { Name = "SqlHostCreds", Rx = new Regex(@"database\.windows\.net[^\r\n]{0,400}?" + Boundary + @"(?:password|pwd|user\s*id|uid)\s*=(?![=>~])\s*",Ci) },
        };

        static readonly TextRule[] WholeRules =
        {
            new TextRule { Name = "SqlHostCreds", Keyword = false, Rx = new Regex(@"://[^/\s:@]{1,100}:[^/\s@]{1,200}@[^\s/]*database\.windows\.net", Ci) },
            new TextRule { Name = "Jwt", Keyword = false, Rx = new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{10,}", Cs) },
            new TextRule { Name = "PrivateKey", Keyword = false, Rx = new Regex(@"-----BEGIN [A-Z ]{0,30}PRIVATE KEY-----\s*[A-Za-z0-9+/=]{16}", Cs) },
        };

        // Strings worth running the text rules on (cheap pre-filter for the many short strings read out of metadata).
        static readonly Regex Trigger = new Regex(@"pass|pwd|user|uid|key|secret|eyJ|PRIVATE|windows\.net", Ci);

        // A value that is exactly one of these is not a credential: format items, template variables, printf conversions,
        // XAML markup extensions, <your password>, a mask of asterisks.
        const string PlaceholderToken =
            @"\{\d+(?:,-?\d+)?(?::[^{}]*)?\}|\{[A-Za-z_][\w.]*\}|\{\{[A-Za-z_][\w.]*\}\}|\{[ \t]*\$[A-Za-z_][\w-]*[ \t]*\}|\$\{[^}\s]{1,60}\}|\$\([^)\s]{1,60}\)" +
            @"|%[-+ #0]*\d*(?:\.\d+)?(?:hh|h|ll|l|L|q|j|z|t)?[diouxXeEfgGcsp]|%\d+\$[sd]|%[A-Za-z_]\w{0,60}%";
        static readonly Regex Placeholder = new Regex(
            @"^(?:" + PlaceholderToken + @"|<[A-Za-z][\w \-]{0,40}>|\*+" +
            @"|\{(?:Binding|TemplateBinding|StaticResource|DynamicResource|x:Static|x:Null|x:Type|RelativeSource)(?:[ ,][^{}]*)?\})$", Cs);
        // A template: one or more placeholders with only punctuation around them ("%s%u%s", "(%5u/%8U)", "%lu)", "{0}{1}").
        // A value that has letters or digits besides its placeholders ({abc}!secret, %abc%-secret) is a value.
        static readonly Regex PlaceholderTokens = new Regex(PlaceholderToken, Cs);
        // After a binary-mode value: more words on the same line that are not a further key=value pair. "password = Display the id
        // as ...", "passwd: password updated successfully" are messages; "password=Secret123 host=db" (libpq) is not prose.
        static readonly Regex ProseAfter = new Regex(@"^[ \t]+[^\s=:;,'""<>]+(?![^\s=:;,'""<>]*[=:])", Cs);
        // "UID= PARTLABEL=": the key has no value; the spaces led to the next key.
        static readonly Regex NextKey = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*[=:]$", Cs);
        static readonly Regex AnglePlaceholder = new Regex(@"^<[A-Za-z][\w \-]{0,40}>", Cs);
        // A user id that is a GUID (uid=<guid>, urn:uuid:<guid>/...) identifies someone; it is not a secret.
        static readonly Regex Guid = new Regex(@"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?(?![0-9A-Za-z])", Cs);
        static readonly Regex XamlMarkup = new Regex(@"^\{[A-Za-z][\w:.]*(?:\s[^""]*)?\}$", Cs);
        static readonly Regex CsAssignment = new Regex(@"^[A-Za-z_]+ = \S", Cs);
        static readonly Regex CsIdentifierChain = new Regex(@"^[A-Za-z_@]\w*(?:\.[A-Za-z_@]\w*)*(?:\(\))?$", Cs);

        static readonly Encoding Latin1 = Encoding.Latin1;
        static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

        // ------------------------------------------------------------------ views
        abstract class View
        {
            public string Enc;
            public string Family;      // dedupe family: "8", "16" or the metadata label
            public string Text;
            public int BytesPerChar = 1;
            public abstract long ByteOffset(int index);
            public abstract string Tail(int index);
        }

        sealed class Utf8View : View
        {
            readonly byte[] _buf; readonly int _len; readonly long _base;
            public Utf8View(byte[] buf, int len, long @base) { _buf = buf; _len = len; _base = @base; Enc = "UTF-8"; Family = "8"; Text = Latin1.GetString(buf, 0, len); }
            public override long ByteOffset(int index) { return _base + index; }
            public override string Tail(int index)
            {
                var sb = new StringBuilder();
                int p = index;
                while (p < _len && sb.Length < MaxValueChars)
                {
                    int cp, n = DecodeUtf8(_buf, p, _len, out cp);
                    if (n == 0 || !IsValueCodePoint(cp)) break;
                    if (cp > 0xFFFF) sb.Append(char.ConvertFromUtf32(cp)); else sb.Append((char)cp);
                    p += n;
                }
                return sb.ToString();
            }
        }

        sealed class Utf16View : View
        {
            readonly long _base; readonly int _shift;
            public Utf16View(byte[] buf, int len, long @base, int shift, bool bigEndian)
            {
                _base = @base; _shift = shift; BytesPerChar = 2; Family = "16";
                Enc = bigEndian ? "UTF-16BE" : "UTF-16LE";
                int n = ((len - shift) >> 1) * 2;
                Text = n >= 2 ? (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(buf, shift, n) : "";
            }
            public override long ByteOffset(int index) { return _base + _shift + 2L * index; }
            public override string Tail(int index) { return TailOf(Text, index, true); }
        }

        sealed class StringView : View
        {
            readonly long _offset;
            public StringView(string s, string enc, long offset) { Text = s; Enc = enc; Family = enc; _offset = offset; }
            public override long ByteOffset(int index) { return _offset + index; }
            public override string Tail(int index) { return TailOf(Text, index, false); }
        }

        static string TailOf(string t, int index, bool utf16Heap)
        {
            int end = index;
            while (end < t.Length && end - index < MaxValueChars)
            {
                char c = t[end];
                if (char.IsHighSurrogate(c)) { if (end + 1 < t.Length && char.IsLowSurrogate(t[end + 1])) { end += 2; continue; } break; }
                if (char.IsLowSurrogate(c) || !IsValueCodePoint(c)) break;
                // In a .NET #US heap a literal ends with a terminal byte (0 or 1) followed by the next entry's length, which is odd
                // (UTF-16 bytes + 1) or starts 0x80-0xDF (2- or 4-byte length). Seen as a UTF-16 unit that is low byte 0/1, high byte odd
                // or 0x80-0xDF: the end of this string, not a character of the value.
                if (utf16Heap && IsUsBoundary(c)) break;
                end++;
            }
            return t.Substring(index, end - index);
        }

        static bool IsValueCodePoint(int cp)
        {
            if (cp == '\t') return true;
            if (cp < 0x20 || cp == 0x7F || (cp >= 0x80 && cp <= 0x9F)) return false;
            if (cp == 0xFFFD || cp == 0xFFFE || cp == 0xFFFF) return false;
            return true;
        }

        // Length of one valid UTF-8 sequence at p (0 if invalid), with its code point.
        static int DecodeUtf8(byte[] b, int p, int end, out int cp)
        {
            cp = 0;
            int c = b[p];
            if (c < 0x80) { cp = c; return 1; }
            int n; int min;
            if (c >= 0xC2 && c <= 0xDF) { n = 2; cp = c & 0x1F; min = 0x80; }
            else if (c >= 0xE0 && c <= 0xEF) { n = 3; cp = c & 0x0F; min = 0x800; }
            else if (c >= 0xF0 && c <= 0xF4) { n = 4; cp = c & 0x07; min = 0x10000; }
            else return 0;
            if (p + n > end) return 0;
            for (int i = 1; i < n; i++)
            {
                int cc = b[p + i];
                if ((cc & 0xC0) != 0x80) return 0;
                cp = (cp << 6) | (cc & 0x3F);
            }
            if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) return 0;
            return n;
        }

        // ------------------------------------------------------------------ values and decisions
        static bool IsValueTerminator(char c)
        {
            return char.IsWhiteSpace(c) || c == ';' || c == '"' || c == '\'' || c == ',' || c == '<' || c == '>' || c == '`';
        }

        // The value after the separator: a quoted string, an ODBC {brace-quoted} value, <placeholder>, or a run of characters up to
        // a delimiter. consumed = how many tail characters the value (with its quotes) used.
        static string ParseValue(string t, out int consumed)
        {
            consumed = 0;
            if (t.Length == 0) return "";
            char c = t[0];
            if (c == '"' || c == '\'')
            {
                int close = t.IndexOf(c, 1);
                if (close > 0) { consumed = close + 1; return t.Substring(1, close - 1); }
                int k0 = 1;
                while (k0 < t.Length && !IsValueTerminator(t[k0])) k0++;
                consumed = k0;
                return t.Substring(1, k0 - 1);
            }
            if (IsValueTerminator(c) && c != '<') return "";   // a value may start with < (but not with > ; , or a quote)
            int start = 1;
            if (c == '{')
            {
                int j = 1;
                while (j < t.Length)
                {
                    if (t[j] == '}') { if (j + 1 < t.Length && t[j + 1] == '}') { j += 2; continue; } break; }
                    j++;
                }
                if (j < t.Length) start = j + 1;
            }
            else if (c == '<')
            {
                var m = AnglePlaceholder.Match(t);
                if (m.Success) start = m.Length;
            }
            int k = start;
            while (k < t.Length && !IsValueTerminator(t[k])) k++;
            consumed = k;
            return t.Substring(0, k);
        }

        static int CodePoints(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) { if (!char.IsLowSurrogate(s[i])) n++; }
            return n;
        }

        static bool IsCredentialValue(string rule, string value, bool binary, bool context)
        {
            if (value.Length == 0 || Placeholder.IsMatch(value)) return false;
            if (rule == "UserIdValue" && Guid.IsMatch(value)) return false;
            if (IsTemplate(value)) return false;
            if (!binary) return true;
            return context || CodePoints(value) >= 6;
        }

        static bool IsTemplate(string value)
        {
            if (!PlaceholderTokens.IsMatch(value)) return false;
            string rest = PlaceholderTokens.Replace(value, "");
            foreach (char c in rest) if (char.IsLetterOrDigit(c)) return false;
            return true;
        }

        static string RuleFor(string keyword)
        {
            string k = keyword.ToLowerInvariant();
            if (k.StartsWith("pass") || k == "pwd") return "PasswordValue";
            if (k.StartsWith("user") || k == "uid") return "UserIdValue";
            return "SecretValue";
        }

        static string Sha(string s)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        }

        // ------------------------------------------------------------------ per-file context
        sealed class Ctx
        {
            public string Label;
            public string Name;
            public string SourcePath;
            public bool Member;
            public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
            public int Count;
        }

        Ctx Child(Ctx parent, string entryName)
        {
            string leaf = entryName.Replace('\\', '/');
            int slash = leaf.TrimEnd('/').LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            return new Ctx { Label = parent.Label + "!/" + entryName, Name = leaf, Member = true };
        }

        void Report(Ctx ctx, string rule, View view, int index, string matchText, int line, string lineSha)
        {
            long offset = view.ByteOffset(index);
            // UTF-16LE text at byte o is also read by the UTF-16BE view at o - 1 (and BE at o by LE at o + 1): one finding, not two.
            string key = rule + "|" + view.Family + "|" + (view.Family == "16" ? (offset + (view.Enc == "UTF-16BE" ? 1 : 0)) >> 1 : offset);
            if (!ctx.Seen.Add(key)) return;
            string sha = Sha(matchText);
            if (ctx.SourcePath != null)
            {
                string entry = ctx.SourcePath + "|" + rule + "|" + lineSha;
                if (lineSha != null && SourceAllow.Contains(entry)) { AllowListed++; AllowUsed.Add(entry); return; }
            }
            else
            {
                string entry = ctx.Name.ToLowerInvariant() + "|" + rule + "|" + sha;
                if (BinaryAllow.Contains(entry)) { AllowListed++; AllowUsed.Add(entry); return; }
            }
            ctx.Count++;
            if (ctx.Count > MaxFindingsPerFile) { FindingsOverCap++; return; }
            Findings.Add(new Finding { File = ctx.Label, Rule = rule, Encoding = view.Enc, Offset = offset, MatchSha256 = sha, Line = line, LineSha256 = lineSha });
        }

        // ------------------------------------------------------------------ rules over a view
        void ScanBinaryView(View v, Ctx ctx)
        {
            foreach (Match m in BinaryKeyword.Matches(v.Text))
            {
                int consumed;
                string tail = v.Tail(m.Index + m.Length);
                string value = ParseValue(tail, out consumed);
                if (value.Length == 0) continue;
                char last = m.Value[m.Value.Length - 1];
                if ((last == ' ' || last == '\t') && NextKey.IsMatch(value)) continue;
                string rule = RuleFor(m.Groups["kw"].Value);
                bool context = HasContext(v, m, rule == "UserIdValue");
                bool quoted = tail.Length > 0 && (tail[0] == '"' || tail[0] == '\'' || tail[0] == '{');
                if (!context && !quoted && ProseAfter.IsMatch(tail.Substring(Math.Min(consumed, tail.Length)))) continue;
                if (!IsCredentialValue(rule, value, true, context)) continue;
                Report(ctx, rule, v, m.Index, m.Value + tail.Substring(0, Math.Min(consumed, tail.Length)), 0, null);
            }
            ScanWholeRules(v, ctx);
        }

        // A connection-string key within 300 bytes, in the same string: the window stops at a line break, NUL or other control
        // character (and, in UTF-16, at a #US entry boundary), because a connection string is one line of text.
        static bool HasContext(View v, Match m, bool userIdKeyword)
        {
            int w = ContextBytes / v.BytesPerChar;
            string t = v.Text;
            int from = m.Index, to = m.Index + m.Length;
            while (from > 0 && m.Index - from < w && !EndsString(t[from - 1], v.BytesPerChar == 2)) from--;
            while (to < t.Length && to - m.Index < w && !EndsString(t[to], v.BytesPerChar == 2)) to++;
            for (var c = ContextKey.Match(t, from, to - from); c.Success; c = c.NextMatch())
            {
                if (c.Index + c.Length > m.Index && c.Index < m.Index + m.Length) continue;
                if (userIdKeyword && c.Groups["id"].Success) continue;
                return true;
            }
            return false;
        }

        static bool EndsString(char c, bool utf16)
        {
            if (c < 0x20 && c != '\t') return true;
            return utf16 && IsUsBoundary(c);
        }

        static bool IsUsBoundary(char c)
        {
            int hi = c >> 8;
            return (c & 0xFE) == 0 && hi != 0 && ((hi & 1) == 1 || (hi >= 0x80 && hi <= 0xDF));
        }

        void ScanWholeRules(View v, Ctx ctx)
        {
            foreach (var r in WholeRules)
                foreach (Match m in r.Rx.Matches(v.Text)) Report(ctx, r.Name, v, m.Index, m.Value, 0, null);
        }

        void ScanTextView(View v, Ctx ctx, string sourceExt, bool[] code)
        {
            foreach (var r in TextRules)
            {
                if (r.SourceOnly && sourceExt == null) continue;
                foreach (Match m in r.Rx.Matches(v.Text))
                {
                    string tail = v.Tail(m.Index + m.Length);
                    int consumed;
                    string value = ParseValue(tail, out consumed);
                    if (!IsCredentialValue(r.Name, value, false, false)) continue;
                    int line = 0; string lineSha = null;
                    if (sourceExt != null)
                    {
                        if ((r.Name == "PasswordValue" || r.Name == "UserIdValue") && IsBenignSource(sourceExt, v.Text, m, tail, consumed, value, code)) continue;
                        LineOf(v.Text, m.Index, out line, out lineSha);
                    }
                    Report(ctx, r.Name, v, m.Index, m.Value + tail.Substring(0, Math.Min(consumed, tail.Length)), line, lineSha);
                }
            }
            foreach (var r in WholeRules)
            {
                foreach (Match m in r.Rx.Matches(v.Text))
                {
                    int line = 0; string lineSha = null;
                    if (sourceExt != null) LineOf(v.Text, m.Index, out line, out lineSha);
                    Report(ctx, r.Name, v, m.Index, m.Value, line, lineSha);
                }
            }
        }

        static void LineOf(string text, int index, out int line, out string lineSha)
        {
            int ls = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
            int le = text.IndexOf('\n', index); if (le < 0) le = text.Length;
            string lineText = text.Substring(ls, le - ls).TrimEnd('\r');
            line = 1; for (int i = 0; i < ls; i++) if (text[i] == '\n') line++;
            lineSha = Convert.ToHexString(SHA256.HashData(Latin1.GetBytes(lineText))).ToLowerInvariant();
        }

        // Source precision: a XAML markup extension is not a value; a C# assignment from an identifier or member, written with
        // spaces around "=", is code when the keyword is in code (not in a string literal of any kind, not in a comment).
        static bool IsBenignSource(string ext, string text, Match m, string tail, int consumed, string value, bool[] code)
        {
            if (ext == ".xaml" || ext == ".axaml")
                return consumed > 0 && tail.Length > 0 && tail[0] == '"' && XamlMarkup.IsMatch(value);
            if (code == null) return false;
            if (!code[m.Index]) return false;
            string matchText = m.Value + tail.Substring(0, Math.Min(consumed, tail.Length));
            if (!CsAssignment.IsMatch(matchText)) return false;
            if (tail.Length > 0 && (tail[0] == '"' || tail[0] == '\'')) return false;
            return CsIdentifierChain.IsMatch(value);
        }

        // true for each character of C# code; false inside comments, string literals (regular, verbatim, interpolated, raw) and chars.
        public static bool[] ClassifyCSharp(string t)
        {
            var code = new bool[t.Length + 1];
            int i = 0, n = t.Length;
            while (i < n)
            {
                char c = t[i];
                char d = i + 1 < n ? t[i + 1] : '\0';
                int start = i;
                if (c == '/' && d == '/') { while (i < n && t[i] != '\n') i++; continue; }
                if (c == '/' && d == '*') { int e = t.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? n : e + 2; continue; }
                // prefixes: $, $$..., @, $@, @$
                int p = i; bool verbatim = false;
                while (p < n && (t[p] == '$' || t[p] == '@')) { if (t[p] == '@') verbatim = true; p++; }
                if (p < n && t[p] == '"' && (p > i || c == '"'))
                {
                    int q = 0; while (p + q < n && t[p + q] == '"') q++;
                    if (q >= 3)
                    {
                        string close = new string('"', q);
                        int e = t.IndexOf(close, p + q, StringComparison.Ordinal);
                        i = e < 0 ? n : e + q;
                        while (i < n && t[i] == '"') i++;
                        continue;
                    }
                    int j = p + 1;
                    if (verbatim)
                    {
                        while (j < n) { if (t[j] == '"') { if (j + 1 < n && t[j + 1] == '"') { j += 2; continue; } break; } j++; }
                    }
                    else
                    {
                        while (j < n && t[j] != '"' && t[j] != '\n') { if (t[j] == '\\') j++; j++; }
                    }
                    i = Math.Min(n, j + 1);
                    continue;
                }
                if (c == '\'')
                {
                    int j = i + 1, lim = Math.Min(n, i + 12);
                    while (j < lim && t[j] != '\'' && t[j] != '\n') { if (t[j] == '\\') j++; j++; }
                    i = Math.Min(n, j + 1);
                    continue;
                }
                code[i] = true;
                i = start + 1;
            }
            return code;
        }

        // ------------------------------------------------------------------ files and streams
        public void ScanFile(string path, string sourceRel)
        {
            FilesScanned++;
            if (string.IsNullOrEmpty(sourceRel)) sourceRel = null;   // PowerShell passes $null as ""
            var ctx = new Ctx { Label = path, Name = Path.GetFileName(path), SourcePath = sourceRel };
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                {
                    if (sourceRel != null) ScanSource(fs, ctx);
                    else ScanStream(fs, ctx, 0);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Errors.Add("could not read " + path + ": " + e.Message);
            }
            catch (Exception e)
            {
                // Never a silent pass: a file the scanner could not finish makes the scan exit 2.
                Errors.Add("could not scan " + path + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        void ScanSource(Stream s, Ctx ctx)
        {
            if (s.Length > 64L * 1024 * 1024) { ctx.SourcePath = null; ScanStream(s, ctx, 0); return; }
            var buf = new byte[s.Length];
            ReadFull(s, buf, 0, buf.Length);
            // A UTF-16 (or otherwise binary) "source" file gets the binary rules, not the source precision.
            if (Array.IndexOf(buf, (byte)0) >= 0) { ctx.SourcePath = null; ScanStream(s, ctx, 0); return; }
            BytesScanned += buf.Length;
            string ext = Path.GetExtension(ctx.SourcePath).ToLowerInvariant();
            var view = new Utf8View(buf, buf.Length, 0);
            bool[] code = (ext == ".cs" || ext == ".csx" || ext == ".razor" || ext == ".cshtml") ? ClassifyCSharp(view.Text) : null;
            ScanTextView(view, ctx, ext, code);
        }

        void ScanStream(Stream s, Ctx ctx, int depth)
        {
            long length = s.Length;
            var head = new byte[(int)Math.Min(8192, length)];
            s.Position = 0;
            ReadFull(s, head, 0, head.Length);
            s.Position = 0;
            List<long[]> mask = null;
            if (depth < MaxArchiveDepth)
            {
                if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B && ScanGzip(s, ctx, depth)) return;
                if (head.Length >= 263 && head[257] == 'u' && head[258] == 's' && head[259] == 't' && head[260] == 'a' && head[261] == 'r' && ScanTar(s, ctx, depth)) return;
                mask = ScanZip(s, ctx, depth);
            }
            if (head.Length >= 2 && head[0] == 'M' && head[1] == 'Z' && length <= MaxEntryBytes) ScanMetadata(s, ctx);
            RawScan(s, ctx, mask);
        }

        void RawScan(Stream s, Ctx ctx, List<long[]> mask)
        {
            var buf = new byte[ChunkSize + Overlap];
            long basePos = 0;
            int carry = 0;
            s.Position = 0;
            while (true)
            {
                int read = ReadFull(s, buf, carry, ChunkSize);
                if (read == 0) break;
                BytesScanned += read;
                int len = carry + read;
                if (mask != null)
                {
                    foreach (var r in mask)
                    {
                        long a = Math.Max(r[0], basePos + carry), b = Math.Min(r[1], basePos + len);
                        if (a < b) Array.Clear(buf, (int)(a - basePos), (int)(b - a));
                    }
                }
                ScanBuffer(buf, len, basePos, ctx, !ctx.Member && IsTextChunk(buf, len));
                if (read < ChunkSize) break;
                int keep = Math.Min(Overlap, len);
                Array.Copy(buf, len - keep, buf, 0, keep);
                basePos += len - keep;
                carry = keep;
            }
        }

        static bool IsTextChunk(byte[] buf, int len)
        {
            int controls = 0;
            for (int i = 0; i < len; i++)
            {
                byte b = buf[i];
                if (b == 0) return false;
                if (b < 0x20 && b != 9 && b != 10 && b != 13 && b != 12 && b != 11 && b != 27) controls++;
            }
            return controls * 100 <= len;
        }

        void ScanBuffer(byte[] buf, int len, long basePos, Ctx ctx, bool text)
        {
            var u8 = new Utf8View(buf, len, basePos);
            if (text) { ScanTextView(u8, ctx, null, null); return; }
            ScanBinaryView(u8, ctx);
            for (int shift = 0; shift <= 1; shift++)
            {
                ScanBinaryView(new Utf16View(buf, len, basePos, shift, false), ctx);
                ScanBinaryView(new Utf16View(buf, len, basePos, shift, true), ctx);
            }
        }

        // A string read out of metadata: the text rules (the string starts at its real start, so the word boundary is meaningful).
        void ScanString(string s, Ctx ctx, string enc, long offset)
        {
            if (s.Length < 5 || !Trigger.IsMatch(s)) return;
            ScanTextView(new StringView(s, enc, offset), ctx, null, null);
        }

        // ------------------------------------------------------------------ archives
        bool ScanGzip(Stream s, Ctx ctx, int depth)
        {
            string tmp = Path.GetTempFileName();
            try
            {
                using (var outFs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose))
                {
                    try
                    {
                        s.Position = 0;
                        using (var gz = new GZipStream(s, CompressionMode.Decompress, true)) CopyLimited(gz, outFs, 4 * MaxEntryBytes);
                    }
                    catch (InvalidDataException) { s.Position = 0; return false; }
                    string name = ctx.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? ctx.Name.Substring(0, ctx.Name.Length - 3)
                                : ctx.Name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ? ctx.Name.Substring(0, ctx.Name.Length - 4) + ".tar" : ctx.Name + ".gunzip";
                    var child = Child(ctx, name);
                    FilesScanned++;
                    ScanStream(outFs, child, depth + 1);
                    return true;
                }
            }
            catch (EntryTooLargeException e) { Errors.Add("skipped " + ctx.Label + " (gzip content " + e.Message + ")"); return true; }
            finally { try { File.Delete(tmp); } catch (IOException) { } }
        }

        bool ScanTar(Stream s, Ctx ctx, int depth)
        {
            s.Position = 0;
            int scanned = 0;
            try
            {
                using (var tr = new TarReader(s, true))
                {
                    TarEntry e;
                    while ((e = tr.GetNextEntry(false)) != null)
                    {
                        if (e.EntryType != TarEntryType.RegularFile && e.EntryType != TarEntryType.V7RegularFile && e.EntryType != TarEntryType.ContiguousFile) continue;
                        if (e.Length == 0 || e.DataStream == null) continue;
                        if (e.Length > MaxEntryBytes) { Errors.Add("skipped " + ctx.Label + "!/" + e.Name + " (larger than " + (MaxEntryBytes >> 20) + " MB)"); continue; }
                        var child = Child(ctx, e.Name);
                        FilesScanned++;
                        using (var ms = Seekable(e.DataStream, e.Length)) ScanStream(ms, child, depth + 1);
                        scanned++;
                    }
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is FormatException || ex is EndOfStreamException || ex is ArgumentException)
            {
                if (scanned > 0) Errors.Add("tar " + ctx.Label + " could not be read to the end: " + ex.Message);
                s.Position = 0;
                return false;
            }
        }

        // Opens a zip anywhere in the file whose end-of-central-directory is at the end (plain zips and installers with an
        // appended zip, such as Velopack Setup.exe), scans every entry, and returns the byte ranges of the entries' (compressed)
        // data so the raw scan does not read compressed bytes as text. Null when the file is not a readable zip.
        List<long[]> ScanZip(Stream s, Ctx ctx, int depth)
        {
            long length = s.Length;
            if (length < 22) return null;
            int tailLen = (int)Math.Min(length, 65557);
            var tail = new byte[tailLen];
            s.Position = length - tailLen;
            ReadFull(s, tail, 0, tailLen);
            int eocd = -1;
            for (int i = tailLen - 22; i >= 0; i--)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 5 && tail[i + 3] == 6) { eocd = i; break; }
            }
            if (eocd < 0) return null;
            long eocdPos = length - tailLen + eocd;
            long cdSize = BitConverter.ToUInt32(tail, eocd + 12);
            long cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
            int entries = BitConverter.ToUInt16(tail, eocd + 10);
            bool zip64 = cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF || entries == 0xFFFF;
            long start = zip64 ? 0 : eocdPos - cdSize - cdOffset;
            if (start < 0) return null;
            var mask = new List<long[]>();
            if (!zip64)
            {
                var cd = new byte[cdSize];
                s.Position = start + cdOffset;
                if (ReadFull(s, cd, 0, cd.Length) != cd.Length || cd.Length < 46 || cd[0] != 0x50 || cd[1] != 0x4B || cd[2] != 1 || cd[3] != 2) return null;
                int p = 0;
                var lh = new byte[30];
                while (p + 46 <= cd.Length && cd[p] == 0x50 && cd[p + 1] == 0x4B && cd[p + 2] == 1 && cd[p + 3] == 2)
                {
                    long comp = BitConverter.ToUInt32(cd, p + 20);
                    int nameLen = BitConverter.ToUInt16(cd, p + 28), extraLen = BitConverter.ToUInt16(cd, p + 30), commentLen = BitConverter.ToUInt16(cd, p + 32);
                    long lho = start + BitConverter.ToUInt32(cd, p + 42);
                    s.Position = lho;
                    if (ReadFull(s, lh, 0, 30) == 30 && lh[0] == 0x50 && lh[1] == 0x4B && lh[2] == 3 && lh[3] == 4)
                    {
                        long data = lho + 30 + BitConverter.ToUInt16(lh, 26) + BitConverter.ToUInt16(lh, 28);
                        mask.Add(new[] { data, data + comp });
                    }
                    p += 46 + nameLen + extraLen + commentLen;
                }
            }
            int scanned = 0;
            try
            {
                using (var sub = new SubStream(s, start, length - start))
                using (var zip = new ZipArchive(sub, ZipArchiveMode.Read, true))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Length == 0 || entry.FullName.EndsWith("/")) continue;
                        string label = ctx.Label + "!/" + entry.FullName;
                        if (entry.Length > MaxEntryBytes) { Errors.Add("skipped " + label + " (larger than " + (MaxEntryBytes >> 20) + " MB)"); continue; }
                        var child = Child(ctx, entry.FullName);
                        FilesScanned++;
                        using (var es = entry.Open())
                        using (var ms = Seekable(es, entry.Length)) ScanStream(ms, child, depth + 1);
                        scanned++;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                if (scanned > 0) Errors.Add("zip " + ctx.Label + " could not be read to the end: " + ex.Message);
                return null;
            }
            finally { s.Position = 0; }
            return zip64 ? null : mask;
        }

        // ------------------------------------------------------------------ .NET metadata
        void ScanMetadata(Stream s, Ctx ctx)
        {
            s.Position = 0;
            try
            {
                using (var pe = new PEReader(s, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchEntireImage))
                {
                    if (!pe.HasMetadata) return;
                    var md = pe.GetMetadataReader();
                    if (md.GetHeapSize(HeapIndex.UserString) > 1)
                    {
                        for (var h = MetadataTokens.UserStringHandle(1); !h.IsNil; h = md.GetNextHandle(h))
                            ScanString(md.GetUserString(h), ctx, "metadata:user-string", MetadataTokens.GetHeapOffset(h));
                    }
                    foreach (var cah in md.CustomAttributes)
                    {
                        var ca = md.GetCustomAttribute(cah);
                        var blob = md.GetBlobBytes(ca.Value);
                        ScanLengthPrefixed(blob, ctx, "metadata:custom-attribute", MetadataTokens.GetHeapOffset(ca.Value));
                    }
                    int constants = md.GetTableRowCount(TableIndex.Constant);
                    for (int i = 1; i <= constants; i++)
                    {
                        var c = md.GetConstant(MetadataTokens.ConstantHandle(i));
                        if (c.TypeCode != ConstantTypeCode.String) continue;
                        var br = md.GetBlobReader(c.Value);
                        ScanString(br.ReadUTF16(br.Length), ctx, "metadata:constant", MetadataTokens.GetHeapOffset(c.Value));
                    }
                    var dir = pe.PEHeaders.CorHeader.ResourcesDirectory;
                    int dirOffset;
                    if (dir.Size > 0 && pe.PEHeaders.TryGetDirectoryOffset(dir, out dirOffset))
                    {
                        var block = pe.GetSectionData(dir.RelativeVirtualAddress);
                        foreach (var rh in md.ManifestResources)
                        {
                            var res = md.GetManifestResource(rh);
                            if (!res.Implementation.IsNil) continue;
                            var r = block.GetReader();
                            r.Offset = (int)res.Offset;
                            int len = r.ReadInt32();
                            ScanResource(md.GetString(res.Name), r.ReadBytes(len), ctx, dirOffset + res.Offset + 4);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException)
            {
                MetadataErrors++;
            }
            finally { s.Position = 0; }
        }

        // fileOffset: where the resource's bytes start in the file. Strings inside a .resources set are reported at fileOffset + their
        // index (the reader does not expose positions); that keeps each one a separate finding.
        void ScanResource(string name, byte[] data, Ctx ctx, long fileOffset)
        {
            if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 0xBEEFCACE)
            {
                try
                {
                    using (var rr = new ResourceReader(new MemoryStream(data, false)))
                    {
                        var keys = new List<string>();
                        var e = rr.GetEnumerator();
                        while (e.MoveNext()) keys.Add((string)e.Key);
                        for (int ki = 0; ki < keys.Count; ki++)
                        {
                            string key = keys[ki];
                            string type; byte[] rd;
                            rr.GetResourceData(key, out type, out rd);
                            if (type == "ResourceTypeCode.String")
                            {
                                int p = 0, n = 0, shift = 0;
                                while (p < rd.Length) { int b = rd[p++]; n |= (b & 0x7F) << shift; shift += 7; if (b < 0x80) break; }
                                if (p + n <= rd.Length) ScanString(Encoding.UTF8.GetString(rd, p, n), ctx, "metadata:resource", fileOffset + ki);
                            }
                            else if ((type == "ResourceTypeCode.Stream" || type == "ResourceTypeCode.ByteArray") && rd.Length >= 4)
                            {
                                int n = BitConverter.ToInt32(rd, 0);
                                if (n >= 0 && n <= rd.Length - 4) ScanBlob(rd, 4, 4 + n, ctx, fileOffset + ki * 0x10000L);
                            }
                            else ScanLengthPrefixed(rd, ctx, "metadata:resource", fileOffset + ki * 0x10000L);
                        }
                    }
                    return;
                }
                catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException || ex is EndOfStreamException || ex is FormatException)
                {
                    MetadataErrors++;
                }
            }
            ScanBlob(data, 0, data.Length, ctx, fileOffset);
        }

        // An embedded stream (BAML, an embedded file): as text when it is UTF-8 text, and every length-prefixed string inside it.
        void ScanBlob(byte[] data, int start, int end, Ctx ctx, long offset)
        {
            try
            {
                string text = Utf8Strict.GetString(data, start, end - start);
                if (text.IndexOf('\0') < 0) { ScanString(text, ctx, "metadata:resource-text", offset); return; }
            }
            catch (DecoderFallbackException) { }
            var slice = new byte[end - start];
            Array.Copy(data, start, slice, 0, slice.Length);
            ScanLengthPrefixed(slice, ctx, "metadata:resource-stream", offset);
        }

        // Every UTF-8 string preceded by its length, in the ECMA-335 compressed form (custom-attribute SerString) or the 7-bit
        // form of BinaryWriter (.resources, BAML).
        void ScanLengthPrefixed(byte[] b, Ctx ctx, string enc, long baseOffset)
        {
            int n = b.Length;
            if (n < 6) return;
            var runEnd = new int[n + 1];
            var boundary = new bool[n + 1];
            int p = 0;
            while (p < n)
            {
                int runStart = p;
                while (p < n)
                {
                    int cp, len = DecodeUtf8(b, p, n, out cp);
                    if (len == 0 || !(IsValueCodePoint(cp) || cp == '\r' || cp == '\n')) break;
                    boundary[p] = true;
                    p += len;
                }
                for (int q = runStart; q < p; q++) runEnd[q] = p;
                boundary[p] = true;
                runEnd[p] = p;
                if (p == runStart) p++;
            }
            for (int i = 0; i + 1 < n; i++)
            {
                int c = b[i];
                if (c < 0x80) Candidate(b, i + 1, c, runEnd, boundary, ctx, enc, baseOffset);
                else if (i + 2 < n)
                {
                    if (b[i + 1] < 0x80) Candidate(b, i + 2, (c & 0x7F) | (b[i + 1] << 7), runEnd, boundary, ctx, enc, baseOffset);
                    if ((c & 0xC0) == 0x80) Candidate(b, i + 2, ((c & 0x3F) << 8) | b[i + 1], runEnd, boundary, ctx, enc, baseOffset);
                }
            }
        }

        void Candidate(byte[] b, int s, int len, int[] runEnd, bool[] boundary, Ctx ctx, string enc, long baseOffset)
        {
            if (len < 5 || s + len > b.Length || !boundary[s] || runEnd[s] < s + len || !boundary[s + len]) return;
            ScanString(Encoding.UTF8.GetString(b, s, len), ctx, enc, baseOffset + s);
        }

        // ------------------------------------------------------------------ helpers
        sealed class EntryTooLargeException : Exception { public EntryTooLargeException(string m) : base(m) { } }

        static void CopyLimited(Stream from, Stream to, long limit)
        {
            var buf = new byte[1 << 16];
            long total = 0;
            int r;
            while ((r = from.Read(buf, 0, buf.Length)) > 0)
            {
                total += r;
                if (total > limit) throw new EntryTooLargeException("larger than " + (limit >> 20) + " MB");
                to.Write(buf, 0, r);
            }
            to.Position = 0;
        }

        static Stream Seekable(Stream from, long length)
        {
            Stream to = length <= 256L * 1024 * 1024
                ? (Stream)new MemoryStream((int)Math.Max(0, length))
                : new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            from.CopyTo(to);
            to.Position = 0;
            return to;
        }

        static int ReadFull(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int r = s.Read(buf, offset + total, count - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        // A read-only window [start, start + length) of a seekable stream.
        sealed class SubStream : Stream
        {
            readonly Stream _s; readonly long _start, _length; long _pos;
            public SubStream(Stream s, long start, long length) { _s = s; _start = start; _length = length; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _length; } }
            public override long Position { get { return _pos; } set { _pos = value; } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos >= _length) return 0;
                _s.Position = _start + _pos;
                int r = _s.Read(buffer, offset, (int)Math.Min(count, _length - _pos));
                _pos += r;
                return r;
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                _pos = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _pos + offset : _length + offset;
                return _pos;
            }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}

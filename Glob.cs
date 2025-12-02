using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices; //OS detection
using System.Text;
using System.Text.RegularExpressions;

namespace Globbing
{
    public static class Glob
    {
        public static IEnumerable<string> Match(string pattern, GlobOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                yield break;

            options ??= new GlobOptions();

            // Expand braces BEFORE normalization. 
            // This ensures that escaped commas/braces (e.g. "img_{a\,b}.png") are handled 
            // correctly before slashes are normalized.
            var rawPatterns = options.ExpandBraces
                ? GlobPatternParser.ExpandBraces(pattern).ToList()
                : new List<string> { pattern };

            foreach (var rawPat in rawPatterns)
            {
                bool originalHasSep = rawPat.Contains('/') || rawPat.Contains('\\');

                // Normalize separators to '/' for internal processing
                string normalizedPat = PathUtils.NormalizePath(rawPat);

                // Split "Pattern Base" (string prefix) from "Walk Root" (FS location).
                // This ensures patterns like "src/*.cs" respect options.BaseDirectory.

                // 1. Extract the static directory part from the pattern (e.g. "src/foo" from "src/foo/*.cs")
                string patternBase = PathUtils.GetPatternDirectory(normalizedPat);

                // 2. Determine the actual absolute directory to start walking from
                string walkRoot;
                string osPatternBase = PathUtils.ToOsPath(patternBase);

                if (string.IsNullOrEmpty(patternBase) || patternBase == ".")
                {
                    walkRoot = options.BaseDirectory;
                }
                else if (Path.IsPathRooted(osPatternBase))
                {
                    // If the pattern itself is absolute (e.g. "C:/Users/*.txt" or "/var/*.log")
                    walkRoot = PathUtils.TryGetRealPath(osPatternBase);
                }
                else
                {
                    // Combine BaseDirectory with the pattern's prefix
                    string combined = Path.Combine(options.BaseDirectory, osPatternBase);
                    walkRoot = PathUtils.TryGetRealPath(combined);
                }

                // 3. Calculate the pattern relative to the patternBase
                // e.g. if Pattern="src/*.cs", PatternBase="src", RelativePattern="*.cs"
                string relativePattern = normalizedPat;
                if (!string.IsNullOrEmpty(patternBase) && patternBase != ".")
                {
                    var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

                    // Check exact match
                    if (normalizedPat.StartsWith(patternBase, comparison))
                    {
                        relativePattern = normalizedPat.Substring(patternBase.Length);
                    }
                    // Check with trailing slash (e.g. patternBase="src", normalized="src/foo")
                    else if (normalizedPat.StartsWith(patternBase + "/", comparison))
                    {
                        relativePattern = normalizedPat.Substring(patternBase.Length);
                    }

                    // Trim leading slash
                    if (relativePattern.StartsWith('/'))
                        relativePattern = relativePattern.Substring(1);
                }

                var ignoreFilter = new IgnoreFilter(
                    options.IgnorePatterns ?? Array.Empty<string>(),
                    options.AllowNegation,
                    options.CaseSensitive);

                var walker = new GlobWalker(walkRoot, new[] { relativePattern }, options, ignoreFilter);

                foreach (var f in walker.Execute(originalHasSep))
                {
                    yield return f;
                }
            }
        }
    }

    public class GlobOptions
    {
        public string BaseDirectory { get; set; } = Directory.GetCurrentDirectory();
        public IEnumerable<string>? IgnorePatterns { get; set; }
        public bool IncludeDirectories { get; set; } = false;
        public bool FollowSymlinks { get; set; } = false;
        public bool CaseSensitive { get; set; } = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public bool AllowNegation { get; set; } = true;
        public bool ExpandBraces { get; set; } = true;
        public bool Realpath { get; set; } = false;
        public bool Dot { get; set; } = false;
        public bool MatchBase { get; set; } = false;
        /// <summary>
        /// If true, exceptions (like UnauthorizedAccessException) will be thrown. 
        /// If false (default), they are ignored and the directory is skipped.
        /// </summary>
        public bool ThrowOnError { get; set; } = false;
    }

    internal static class PathUtils
    {
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace('\\', '/');
        }

        public static string ToOsPath(string path)
        {
            if (Path.DirectorySeparatorChar == '/') return path;
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        // Returns the non-magic directory portion of a glob pattern.
        public static string GetPatternDirectory(string pattern)
        {
            // Check for UNC paths on Windows (e.g. //server/share/foo*.txt)
            if (IsUncPath(pattern))
            {
                // Find the end of the share name
                int shareEnd = pattern.IndexOf('/', 2);
                if (shareEnd > -1)
                {
                    int pathStart = pattern.IndexOf('/', shareEnd + 1);
                    if (pathStart == -1) return pattern; // The whole thing is the root

                    // Check for magic in the unc root? unlikely but strict check:
                    string root = pattern.Substring(0, pathStart);
                    if (!HasMagic(root)) return root;
                }
            }

            int len = pattern.Length;
            int firstMagic = -1;

            for (int i = 0; i < len; i++)
            {
                char c = pattern[i];
                if (c == '*' || c == '?' || c == '[' || c == '{')
                {
                    firstMagic = i;
                    break;
                }
                if (c == '\\' && i + 1 < len) i++; // Skip escape
            }

            if (firstMagic == -1)
            {
                // No magic, check if it's a file or dir path
                int lastSlash = pattern.LastIndexOf('/');
                if (lastSlash == -1) return IsDriveRelative(pattern) ? FixDriveRoot(pattern.Substring(0, 2)) : "";
                if (lastSlash == 0) return "/";
                return FixDriveRoot(pattern.Substring(0, lastSlash));
            }

            if (firstMagic == 0) return "";

            int cutIndex = pattern.LastIndexOf('/', firstMagic - 1);
            if (cutIndex == -1) return IsDriveRelative(pattern) ? FixDriveRoot(pattern.Substring(0, 2)) : "";
            if (cutIndex == 0) return "/";

            return FixDriveRoot(pattern.Substring(0, cutIndex));
        }

        private static bool HasMagic(string txt)
        {
            return txt.IndexOfAny(new[] { '*', '?', '[', '{' }) > -1;
        }

        private static bool IsUncPath(string path)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                   && path.Length >= 2
                   && path[0] == '/'
                   && path[1] == '/';
        }

        private static bool IsDriveRelative(string path)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                   && path.Length >= 2
                   && path[1] == ':'
                   && char.IsLetter(path[0]);
        }

        private static string FixDriveRoot(string path)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (path.Length == 2 && path[1] == ':' && char.IsLetter(path[0]))
                    return path + "/";
            }
            return path;
        }

        public static string TryGetRealPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        public static bool IsSymlink(FileSystemInfo info)
        {
            if (info.LinkTarget != null) return true;
            // Fallback for older frameworks or specific reparse points
            return (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
    }

    internal class GlobWalker
    {
        private readonly string _root;
        private readonly List<string> _patterns;
        private readonly GlobOptions _options;
        private readonly IgnoreFilter _ignore;
        private readonly Dictionary<string, Regex> _regexCache;

        public GlobWalker(string root, IEnumerable<string> patterns, GlobOptions options, IgnoreFilter ignore)
        {
            _root = string.IsNullOrEmpty(root) ? "." : root;
            _patterns = patterns.ToList();
            _options = options;
            _ignore = ignore;
            var comparer = options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            _regexCache = new Dictionary<string, Regex>(comparer);
        }

        private Regex GetRegex(string pattern)
        {
            if (_regexCache.TryGetValue(pattern, out var regex)) return regex;
            regex = GlobMatcher.GlobSegmentToRegex(pattern, _options.CaseSensitive, _options.Dot);
            _regexCache[pattern] = regex;
            return regex;
        }

        private string ToRelative(string fullPath)
        {
            return PathUtils.NormalizePath(Path.GetRelativePath(_root, fullPath));
        }

        public IEnumerable<string> Execute(bool originalHasSep)
        {
            // Deduplicate results based on the final output format (OS Path or RealPath).
            // This prevents casing differences on Windows from returning duplicates.
            var dedup = new HashSet<string>(_options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

            foreach (var pattern in _patterns)
            {
                if (_options.MatchBase && !originalHasSep && !pattern.Contains('/'))
                {
                    var regex = GetRegex(pattern);
                    foreach (var file in EnumerateFilesMatchBase(_root, regex))
                    {
                        string res = FormatResult(file);
                        if (dedup.Add(res)) yield return res;
                    }
                    continue;
                }

                bool directoryOnly = pattern.EndsWith('/');
                var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var seenDirs = new HashSet<string>(_options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

                foreach (var match in Walk(_root, segments, 0, seenDirs, directoryOnly))
                {
                    string res = FormatResult(match);
                    if (dedup.Add(res)) yield return res;
                }
            }
        }

        private string FormatResult(string path)
        {
            return _options.Realpath ? PathUtils.TryGetRealPath(path) : PathUtils.ToOsPath(path);
        }

        private IEnumerable<string> EnumerateFilesMatchBase(string root, Regex basenameRegex)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            var seen = new HashSet<string>(_options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                string checkDir = dir;
                if (_options.FollowSymlinks) checkDir = PathUtils.TryGetRealPath(dir);
                if (seen.Contains(checkDir)) continue;
                seen.Add(checkDir);

                IEnumerable<FileSystemInfo> entries;
                try
                {
                    var di = new DirectoryInfo(dir);
                    if (!di.Exists) continue;
                    entries = di.EnumerateFileSystemInfos();
                }
                catch
                {
                    if (_options.ThrowOnError) throw;
                    continue;
                }

                foreach (var entry in entries)
                {
                    bool isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;

                    if (_ignore.IsIgnored(ToRelative(entry.FullName))) continue;

                    if (isDir)
                    {
                        // Don't recurse into hidden directories if Dot is false
                        if (!_options.Dot && entry.Name.StartsWith('.')) continue;
                        if (!_options.FollowSymlinks && PathUtils.IsSymlink(entry)) continue;

                        stack.Push(entry.FullName);

                        if (_options.IncludeDirectories && basenameRegex.IsMatch(entry.Name))
                            yield return entry.FullName;
                    }
                    else
                    {
                        if (basenameRegex.IsMatch(entry.Name))
                            yield return entry.FullName;
                    }
                }
            }
        }

        public IEnumerable<string> Walk(string dir, string[] segments, int index, HashSet<string> seenDirs, bool directoryOnly)
        {
            if (_options.FollowSymlinks)
            {
                var realDir = PathUtils.TryGetRealPath(dir);
                if (seenDirs.Contains(realDir)) yield break;
                seenDirs.Add(realDir);
            }

            IEnumerable<FileSystemInfo> entries;
            try
            {
                var di = new DirectoryInfo(dir);
                if (!di.Exists) yield break;
                entries = di.EnumerateFileSystemInfos();
            }
            catch
            {
                if (_options.ThrowOnError) throw;
                yield break;
            }

            if (index == segments.Length)
            {
                if (!_ignore.IsIgnored(ToRelative(dir)))
                {
                    if (directoryOnly || _options.IncludeDirectories)
                        yield return dir;
                }
                yield break;
            }

            string current = segments[index];
            bool isLast = index == segments.Length - 1;

            if (current == "**")
            {
                // 1. Match current dir against remaining segments
                foreach (var match in Walk(dir, segments, index + 1, seenDirs, directoryOnly))
                    yield return match;

                // 2. Recursive walk
                foreach (var entry in entries)
                {
                    bool isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                    if (!isDir) continue;
                    if (!_options.FollowSymlinks && PathUtils.IsSymlink(entry)) continue;

                    if (!_options.Dot && entry.Name.StartsWith('.')) continue;
                    if (_ignore.IsIgnored(ToRelative(entry.FullName))) continue;

                    foreach (var match in Walk(entry.FullName, segments, index, seenDirs, directoryOnly))
                        yield return match;
                }
                yield break;
            }

            var regex = GetRegex(current);

            foreach (var entry in entries)
            {
                if (_ignore.IsIgnored(ToRelative(entry.FullName))) continue;

                bool isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;

                if (!regex.IsMatch(entry.Name)) continue;
                if (!_options.FollowSymlinks && PathUtils.IsSymlink(entry)) continue;

                if (isLast)
                {
                    if (directoryOnly)
                    {
                        if (isDir) yield return entry.FullName;
                    }
                    else
                    {
                        if (!isDir || _options.IncludeDirectories)
                            yield return entry.FullName;
                    }
                }
                else if (isDir)
                {
                    foreach (var match in Walk(entry.FullName, segments, index + 1, seenDirs, directoryOnly))
                        yield return match;
                }
            }
        }
    }

    internal static class GlobMatcher
    {
        public static Regex GlobSegmentToRegex(string segment, bool caseSensitive, bool dotOption = false)
        {
            if (segment == null) throw new ArgumentNullException(nameof(segment));

            var pattern = new StringBuilder();
            pattern.Append('^');

            // Standard Glob behavior: * doesn't match dotfiles unless dot=true.
            // Explicit dot at start of segment bypasses this check.
            bool explicitDot = segment.Length > 0 && segment[0] == '.';
            if (!dotOption && !explicitDot)
            {
                pattern.Append("(?![.])");
            }

            int length = segment.Length;
            for (int i = 0; i < length; i++)
            {
                char c = segment[i];
                switch (c)
                {
                    case '*': pattern.Append("[^/]*"); break;
                    case '?': pattern.Append("[^/]"); break;
                    case '[':
                        int j = i + 1;
                        if (j >= length) { pattern.Append("\\["); break; }
                        bool negated = segment[j] == '!' || segment[j] == '^';
                        if (negated) j++;
                        var classContent = new StringBuilder();
                        if (j < length && segment[j] == ']') { classContent.Append("\\]"); j++; }

                        while (j < length && segment[j] != ']')
                        {
                            char curr = segment[j];
                            // Correctly consume escaped characters inside classes
                            if (curr == '\\')
                            {
                                if (j + 1 < length)
                                {
                                    j++;
                                    classContent.Append(Regex.Escape(segment[j].ToString()));
                                }
                                else
                                {
                                    classContent.Append("\\\\");
                                }
                            }
                            // Handle ranges (e.g. a-z)
                            else if (curr == '-' && classContent.Length > 0 && j + 1 < length && segment[j + 1] != ']')
                            {
                                classContent.Append('-');
                            }
                            else
                            {
                                classContent.Append(Regex.Escape(curr.ToString()));
                            }
                            j++;
                        }

                        if (j >= length) { pattern.Append("\\["); break; }
                        i = j;
                        pattern.Append('[');
                        if (negated) pattern.Append('^');
                        pattern.Append(classContent);
                        pattern.Append(']');
                        break;
                    case '\\':
                        if (i + 1 < length) i++;
                        pattern.Append(Regex.Escape(segment[i].ToString()));
                        break;
                    default:
                        pattern.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }
            pattern.Append('$');

            var options = RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;

            return new Regex(pattern.ToString(), options, TimeSpan.FromSeconds(1));
        }
    }

    internal static class GlobPatternParser
    {
        public static IEnumerable<string> ExpandBraces(string pattern)
        {
            var results = new List<string>();
            Expand(pattern, results);
            return results;
        }

        private static void Expand(string pattern, List<string> results)
        {
            int first = -1, level = 0;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern[i] == '\\') { if (i + 1 < pattern.Length) i++; continue; }
                if (pattern[i] == '{') { if (level == 0) first = i; level++; }
                else if (pattern[i] == '}')
                {
                    level--;
                    if (level == 0 && first != -1)
                    {
                        var prefix = pattern[..first];
                        var body = pattern.Substring(first + 1, i - first - 1);
                        var suffix = pattern[(i + 1)..];
                        var options = SplitOnTopLevelCommas(body);
                        // If no commas, it's just a block {a} -> a (or we could recurse, but standard glob usually splits on commas)
                        if (options.Count == 1) { results.Add(UnescapeBraces(pattern)); return; }
                        foreach (var opt in options) Expand(prefix + opt + suffix, results);
                        return;
                    }
                }
            }
            results.Add(UnescapeBraces(pattern));
        }

        private static string UnescapeBraces(string str) =>
            str.Replace("\\{", "{").Replace("\\}", "}").Replace("\\,", ",");

        private static List<string> SplitOnTopLevelCommas(string input)
        {
            var result = new List<string>();
            int level = 0, last = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '\\') { if (i + 1 < input.Length) i++; continue; }
                if (input[i] == '{') level++;
                else if (input[i] == '}') level--;
                else if (input[i] == ',' && level == 0) { result.Add(input[last..i]); last = i + 1; }
            }
            result.Add(input[last..]);
            return result;
        }
    }

    internal class IgnoreFilter
    {
        private readonly List<(Regex pattern, bool negated, bool matchFullPath)> _matchers = new();

        public IgnoreFilter(IEnumerable<string> patterns, bool allowNegation, bool caseSensitive)
        {
            foreach (var raw in patterns)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string pat = PathUtils.NormalizePath(raw.Trim());

                bool neg = allowNegation && pat.StartsWith('!');
                if (neg) pat = pat[1..];

                if (string.IsNullOrEmpty(pat)) continue;

                bool hasSep = pat.Contains('/');
                // If it has a slash, it's a path pattern (anchored to root).
                // If no slash, it's a name pattern (matches basename).

                Regex regex;
                if (hasSep)
                    regex = BuildPathPatternRegex(pat, caseSensitive);
                else
                    regex = GlobMatcher.GlobSegmentToRegex(pat, caseSensitive, dotOption: false);

                _matchers.Add((regex, neg, hasSep));
            }
        }

        private static Regex BuildPathPatternRegex(string pattern, bool caseSensitive)
        {
            var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return new Regex("$^");

            var sb = new StringBuilder();
            sb.Append('^');

            bool endsWithSlash = pattern.EndsWith('/');
            bool endsWithGlobStar = pattern.EndsWith("/**");

            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0) sb.Append('/');
                string seg = segments[i];
                if (seg == "**") sb.Append(".*");
                else
                {
                    var segRegex = GlobMatcher.GlobSegmentToRegex(seg, caseSensitive, dotOption: false);
                    string s = segRegex.ToString();
                    // strip ^ and $
                    sb.Append(s.Substring(1, s.Length - 2));
                }
            }

            // handling of ignore patterns ending in /**
            // This matches the directory itself AND anything inside.
            if (endsWithGlobStar)
                sb.Append("(/.*)?$");
            else if (endsWithSlash)
                sb.Append(".*$"); // standard behavior for trailing slash in ignores
            else
                sb.Append('$');

            var options = RegexOptions.CultureInvariant;
            if (!caseSensitive) options |= RegexOptions.IgnoreCase;
            return new Regex(sb.ToString(), options, TimeSpan.FromSeconds(1));
        }

        public bool IsIgnored(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath) || relativePath == ".") return false;
            if (relativePath.StartsWith("..")) return false;

            if (_matchers.Count == 0) return false;

            string basename = relativePath;
            int lastSlash = relativePath.LastIndexOf('/');
            if (lastSlash >= 0) basename = relativePath.Substring(lastSlash + 1);

            bool ignored = false;
            foreach (var (pattern, negated, matchFullPath) in _matchers)
            {
                string target = matchFullPath ? relativePath : basename;
                if (pattern.IsMatch(target))
                {
                    ignored = !negated;
                }
            }
            return ignored;
        }
    }
}
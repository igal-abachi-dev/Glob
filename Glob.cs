using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;

public static class Glob
{
    public static IEnumerable<string> Match(string pattern, GlobOptions? options = null)
    {
        options ??= new GlobOptions();
        pattern = PathUtils.NormalizePath(pattern);

        var baseDir = PathUtils.DetermineBaseDir(pattern, options.BaseDirectory);
        var patterns = options.ExpandBraces
            ? GlobPatternParser.ExpandBraces(pattern).ToList()
            : new List<string> { pattern };
        var ignoreFilter = new IgnoreFilter(
            options.IgnorePatterns ?? Array.Empty<string>(),
            options.AllowNegation,
            options.CaseSensitive);

        var walker = new GlobWalker(baseDir, patterns, options, ignoreFilter);

        // Wrap execution to handle realpath deduplication if requested
        if (!options.Realpath)
        {
            foreach (var f in walker.Execute())
                yield return f;
        }
        else
        {
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var seen = new HashSet<string>(comparer);

            foreach (var f in walker.Execute())
            {
                var real = PathUtils.TryGetRealPath(f);
                if (seen.Add(real))
                    yield return real;
            }
        }
    }
}

// --------------------------------------------
// Options and path utility
// --------------------------------------------
public class GlobOptions
{
    public string BaseDirectory { get; set; } = Directory.GetCurrentDirectory();
    public IEnumerable<string>? IgnorePatterns { get; set; }
    public bool IncludeDirectories { get; set; } = false;
    public bool FollowSymlinks { get; set; } = false;
    public bool CaseSensitive { get; set; } = false;
    public bool AllowNegation { get; set; } = true;
    public bool ExpandBraces { get; set; } = true;
    public bool Realpath { get; set; } = false;
    public bool Dot { get; set; } = false; // Match dotfiles/dirs by default
    public bool MatchBase { get; set; } = false; // Match basename if no dir in pattern
}

internal static class PathUtils
{
    public static string NormalizePath(string path)
    {
        // For Windows UNC or drive root, handle correctly
        if (string.IsNullOrEmpty(path)) return path;
        string norm = path.Replace('\\', Path.DirectorySeparatorChar)
                          .Replace('/', Path.DirectorySeparatorChar);
        return norm;
    }

    public static string DetermineBaseDir(string pattern, string defaultBase)
    {
        // Find where first glob magic occurs
        int idx = pattern.IndexOfAny(new[] { '*', '?', '{', '[', '!' });
        if (idx == -1)
            return Path.GetDirectoryName(pattern) ?? defaultBase;

        int lastSlash = pattern.LastIndexOf(Path.DirectorySeparatorChar, idx);
        if (lastSlash < 0) return defaultBase;
        // UNC root: \\server\share\dir
        string baseDir = pattern.Substring(0, lastSlash);
        if (string.IsNullOrWhiteSpace(baseDir))
            return defaultBase;
        return baseDir;
    }

    public static string TryGetRealPath(string path)
    {
        try
        {
            // This resolves symlinks on Unix, or returns canonical on Windows
            return Path.GetFullPath(new FileInfo(path).FullName);
        }
        catch
        {
            return path;
        }
    }

    public static bool IsSymlink(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }
}

// --------------------------------------------
// Main walker
// --------------------------------------------
internal class GlobWalker
{
    private readonly string _root;
    private readonly List<string> _patterns;
    private readonly GlobOptions _options;
    private readonly IgnoreFilter _ignore;
    private readonly HashSet<string> _seenDirs = new HashSet<string>(
        OperatingSystem.IsWindows()
          ? StringComparer.OrdinalIgnoreCase
          : StringComparer.Ordinal
      );

    public GlobWalker(string root, IEnumerable<string> patterns, GlobOptions options, IgnoreFilter ignore)
    {
        _root = string.IsNullOrEmpty(root) ? "." : root;
        _patterns = patterns.ToList();
        _options = options;
        _ignore = ignore;
    }

    public IEnumerable<string> Execute()
    {
        foreach (var pattern in _patterns)
        {
            // If matchBase: match basename of files anywhere under root
            if (_options.MatchBase && !pattern.Contains(Path.DirectorySeparatorChar))
            {
                var regex = GlobMatcher.GlobSegmentToRegex(pattern, _options.CaseSensitive, _options.Dot);
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (regex.IsMatch(name) && !_ignore.IsIgnored(file))
                        yield return _options.Realpath ? PathUtils.TryGetRealPath(file) : file;
                }
                continue;
            }

            var segments = PathUtils.NormalizePath(pattern)
                                   .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var match in Walk(_root, segments, 0))
            {
                yield return _options.Realpath ? PathUtils.TryGetRealPath(match) : match;
            }
        }
    }

    private IEnumerable<string> Walk(string dir, string[] segments, int index)
    {
var realDir = _options.FollowSymlinks
    ? PathUtils.TryGetRealPath(dir)
    : dir;
        if (_seenDirs.Contains(realDir)) yield break; // Prevent infinite symlink cycles
        _seenDirs.Add(realDir);

        IEnumerable<string> entries = Array.Empty<string>();
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { yield break; } // Permission error, skip this dir

        // If we're at the end of segments, yield dir/file
        if (index == segments.Length)
        {
            if (_options.IncludeDirectories && Directory.Exists(dir) && !_ignore.IsIgnored(dir))
                yield return dir;
            yield break;
        }

        string current = segments[index];

        // Handle globstar '**'
        if (current == "**")
        {
            // Match 0 directories: continue to next segment in same dir
            foreach (var match in Walk(dir, segments, index + 1))
                yield return match;

            // Recurse into subdirectories (excluding symlinks if not following)
            foreach (var subdir in entries.Where(e => Directory.Exists(e)))
            {
                if (!_options.FollowSymlinks && PathUtils.IsSymlink(subdir))
                    continue;

                var name = Path.GetFileName(subdir);
                if (!_options.Dot && name.StartsWith(".")) continue; // skip dotdirs if not dot

                if (_ignore.IsIgnored(subdir)) continue;

                foreach (var match in Walk(subdir, segments, index))
                    yield return match;
            }
            yield break;
        }

        var regex = GlobMatcher.GlobSegmentToRegex(current, _options.CaseSensitive, _options.Dot);

        foreach (var entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (!_options.Dot && name.StartsWith(".") && !current.StartsWith(".")) continue; // skip dotfiles/dirs if not dot mode

            if (!_options.FollowSymlinks && PathUtils.IsSymlink(entry)) continue;
            if (!regex.IsMatch(name)) continue;
            if (_ignore.IsIgnored(entry)) continue;

            bool isDir = Directory.Exists(entry);
            bool isLast = index == segments.Length - 1;

            if (isLast && (!isDir || _options.IncludeDirectories))
            {
                yield return entry;
            }
            else if (isDir)
            {
                foreach (var match in Walk(entry, segments, index + 1))
                    yield return match;
            }
        }
    }
}

// --------------------------------------------
// Glob matching and parsing
// --------------------------------------------
internal static class GlobMatcher
{
    // Main matcher: can be extended for extglob etc.
    public static Regex GlobSegmentToRegex(string segment, bool caseSensitive, bool dotMode)
    {
        // Extglob: Node supports patterns like !(foo), ?(bar) - not yet implemented
        string pattern = "^";
        bool inBracket = false;

        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < segment.Length && segment[i + 1] == '*')
                    {
                        pattern += ".*";
                        i++;
                    }
                    else
                    {
                        pattern += dotMode ? ".*" : "[^/]*";  // allow everything (including /) then split handles separators
                    }
                    break;
                case '?':
                    pattern += dotMode ? "." : "[^/]";
                    break;
                case '[':
                    inBracket = true;
                    pattern += "[";
                    break;
                case ']':
                    inBracket = false;
                    pattern += "]";
                    break;
                case '.':
                    pattern += "\\.";
                    break;
                case '\\':
                    pattern += "\\\\";
                    break;
                default:
                    if ("+()^${}|.\".Contains(c))
                        pattern += "\\" + c;
                    else
                        pattern += c;
                    break;
            }
        }
        pattern += "$";
        var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!caseSensitive) opts |= RegexOptions.IgnoreCase;
        return new Regex(pattern, opts);
    }
}

// --------------------------------------------
// Recursive brace expansion: Node-parity
// --------------------------------------------
internal static class GlobPatternParser
{
    // Expands braces: supports nested, escaped, node-like behavior
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
            if (pattern[i] == '\\') { i++; continue; } // skip escaped
            if (pattern[i] == '{')
            {
                if (level == 0) first = i;
                level++;
            }
            else if (pattern[i] == '}')
            {
                level--;
                if (level == 0 && first != -1)
                {
                    var prefix = pattern[..first];
                    var body = pattern.Substring(first + 1, i - first - 1);
                    var suffix = pattern[(i + 1) ..];

                    var options = SplitOnTopLevelCommas(body);
                    if (options.Count == 1)
                    {
                        // No top-level comma → literal braces
                        results.Add(pattern.Replace("\\{", "{").Replace("\\}", "}"));
                        return;
                    }
                    foreach (var opt in options)
                        Expand(prefix + opt + suffix, results);
                    return;
                }
            }
        }
        // No more braces
        results.Add(pattern.Replace("\\{", "{").Replace("\\}", "}")); // unescape
    }

    private static List<string> SplitOnTopLevelCommas(string input)
    {
        var result = new List<string>();
        int level = 0, last = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '\\') { i++; continue; }
            if (input[i] == '{') level++;
            else if (input[i] == '}') level--;
            else if (input[i] == ',' && level == 0)
            {
                result.Add(input[last..i]);
                last = i + 1;
            }
        }
        result.Add(input[last..]);
        return result;
    }
}

// --------------------------------------------
// Ignore/negation logic: Node-like, last-match-wins
// --------------------------------------------
internal class IgnoreFilter
{
    private readonly List<(Regex pattern, bool negated)> _matchers = new();

    public IgnoreFilter(IEnumerable<string> patterns, bool allowNegation, bool caseSensitive)
    {
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            string pat = PathUtils.NormalizePath(raw.Trim());
            bool neg = allowNegation && pat.StartsWith("!");
            string clean = neg ? pat[1..] : pat;

            // Ignore patterns are often path segments; treat them as globs
            var regex = GlobMatcher.GlobSegmentToRegex(clean, caseSensitive, true);
            _matchers.Add((regex, neg));
        }
    }

    // Node: last matching rule wins
    public bool IsIgnored(string path)
    {
        path = PathUtils.NormalizePath(path);

        bool ignored = false;
        foreach (var (pattern, negated) in _matchers)
        {
            if (pattern.IsMatch(path))
                ignored = !negated;
        }
        return ignored;
    }
}

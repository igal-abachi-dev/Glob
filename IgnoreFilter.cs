using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Globbing;

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
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Globbing;

internal class IgnoreFilter
{
    // FIX: Using segment-based matching instead of regex stitching to fix **/ semantics
    private readonly List<(List<object> segments, bool negated, bool matchBase)> _matchers = new();
    private readonly bool _caseSensitive;

    public IgnoreFilter(IEnumerable<string> patterns, bool allowNegation, bool caseSensitive)
    {
        _caseSensitive = caseSensitive;

        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string pat = PathUtils.NormalizePath(raw.Trim());

            bool neg = allowNegation && pat.StartsWith('!');
            if (neg) pat = pat[1..];

            if (string.IsNullOrEmpty(pat)) continue;

            // If it starts with '/', it is anchored to root (matchBase = false).
            // Otherwise, it can match basename or relative path?
            // Standard gitignore: if it contains slash, it is not matchBase.
            bool matchBase = !pat.Contains('/');
            if (pat.StartsWith('/'))
            {
                matchBase = false;
                pat = pat.Substring(1);
            }

            // Remove trailing slash but remember it implies "directory only" ?
            // For simple ignore logic, we usually treat "foo/" as "ignore foo and everything inside".
            // We'll strip it for segment matching.
            if (pat.EndsWith("/")) pat = pat.Substring(0, pat.Length - 1);

            var segments = new List<object>();
            foreach (var seg in pat.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (seg == "**")
                {
                    segments.Add("**");
                }
                else
                {
                    // Compile regex for this segment
                    segments.Add(GlobMatcher.GlobSegmentToRegex(seg, caseSensitive, dotOption: false));
                }
            }

            _matchers.Add((segments, neg, matchBase));
        }
    }

    public bool IsIgnored(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == ".") return false;
        if (relativePath.StartsWith("..")) return false; // Don't filter parent paths

        var pathSegs = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        bool ignored = false;

        foreach (var (patternSegs, negated, matchBase) in _matchers)
        {
            bool match = false;
           
            if (matchBase)
            {
                // Match base name only (last segment) against the pattern
                // But pattern might have multiple segments? usually matchBase implies single segment pattern like "*.o"
                if (patternSegs.Count == 1)
                {
                    string basename = pathSegs[pathSegs.Length - 1];
                    match = MatchSegments(new[] { basename }, patternSegs, 0, 0);
                }
                else
                {
                    // If pattern is "foo/bar" but matchBase was calc'd false earlier, this won't hit.
                    // If pattern is "*.o", we match against basename.
                    // If we are here, it's likely a single segment.
                }
            }
           
            if (!match)
            {
                // Full path match
                match = MatchSegments(pathSegs, patternSegs, 0, 0);
            }

            if (match)
            {
                ignored = !negated;
            }
        }

        return ignored;
    }

    private bool MatchSegments(string[] path, List<object> pattern, int pIdx, int sIdx)
    {
        // Pattern exhausted
        if (pIdx >= pattern.Count)
        {
            // If path also exhausted, it's a match
            if (sIdx >= path.Length) return true;
           
            // Standard ignore behavior: if we matched "bin", we also ignore "bin/debug"
            // So if pattern is exhausted but we matched the prefix of path, return true.
            return true;
        }

        // Path exhausted but pattern remains
        if (sIdx >= path.Length)
        {
            // Only match if remaining pattern segments are all "**"
            for (int i = pIdx; i < pattern.Count; i++)
            {
                if (pattern[i] is not string s || s != "**") return false;
            }
            return true;
        }

        var patSeg = pattern[pIdx];

        if (patSeg is string str && str == "**")
        {
            // Recurse: match zero or more path segments
            // 1. Try consuming zero path segments (move to next pattern)
            if (MatchSegments(path, pattern, pIdx + 1, sIdx)) return true;

            // 2. Try consuming one path segment (move to next path, stay on **)
            if (MatchSegments(path, pattern, pIdx, sIdx + 1)) return true;
           
            return false;
        }
        else if (patSeg is Regex r)
        {
            if (!r.IsMatch(path[sIdx])) return false;
            return MatchSegments(path, pattern, pIdx + 1, sIdx + 1);
        }

        return false;
    }
}
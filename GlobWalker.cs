using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Globbing;

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

    private Regex GetRegex(string pattern) //todo: IPatternProcessor GetCompiledRegex() abstraction instead impl here
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

            //main loop:
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

            //dir found , can have entries
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
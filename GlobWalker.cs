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
        if (_regexCache.TryGetValue(pattern, out var regex)) return regex; //scope of the cache is per-walk , maybe could be larger scope
        regex = GlobMatcher.GlobSegmentToRegex(pattern, _options.CaseSensitive, _options.Dot);
        _regexCache[pattern] = regex;
        return regex;
    }

    private string ToRelative(string fullPath)
    {
        // FIX: Ignores must be checked relative to the BaseDirectory, 
        // not the optimized walk root (e.g. if walking 'src/', ignore 'src/tmp' must match).
        return PathUtils.NormalizePath(Path.GetRelativePath(_options.BaseDirectory, fullPath), _options.WindowsPathsNoEscape);
    }
	
    public IEnumerable<string> Execute(bool originalHasSep, bool forceDirectoryOnly = false)
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
                    if (dedup.Add(file)) yield return file;
                }
                continue;
            }

            bool directoryOnly = forceDirectoryOnly || pattern.EndsWith('/');
            var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var seenDirs = new HashSet<string>(_options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

            //main loop:
            foreach (var match in Walk(_root, segments, 0, seenDirs, directoryOnly))
            {
                if (dedup.Add(match)) yield return match;
            }
        }
    }
	
    // FIX: Updated to handle Absolute and Realpath correctly
    public string FormatResult(FileSystemInfo entry)
    {
        if (_options.Realpath)
        {
            return PathUtils.ResolveSymlink(entry);
        }

        if (_options.Absolute)
        {
            return entry.FullName;
        }

        // FIX: Output paths should be relative to the user's BaseDirectory, 
        // even if we optimized the walk to start deeper in the tree.
        string rel = Path.GetRelativePath(_options.BaseDirectory, entry.FullName);
        // Ensure standard separators for output if desired, or keep OS?
        // Node glob usually returns forward slashes.
        return PathUtils.NormalizePath(rel, _options.WindowsPathsNoEscape);
    }

    private IEnumerable<string> EnumerateFilesMatchBase(string root, Regex basenameRegex)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var seen = new HashSet<string>(_options.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // FIX: Stronger cycle detection
            string checkDir = dir;
            if (_options.FollowSymlinks) checkDir = PathUtils.ResolveSymlink(new DirectoryInfo(dir));

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
                if (_ignore.IsIgnored(ToRelative(entry.FullName))) continue;

                bool isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                bool isSym = PathUtils.IsSymlink(entry);

                if (isDir)
                {
                    // 1. MATCH: Check if the directory itself matches the pattern.
                    // Doing this BEFORE the 'continue' ensures we can find ".config" 
                    // even if we aren't allowed to recurse into it.
                    if (_options.IncludeDirectories && basenameRegex.IsMatch(entry.Name))
                        yield return FormatResult(entry);

                    // 2. RECURSE: Decide if we should walk into this directory.


                    // FIX: Don't recurse into hidden directories if Dot is false
                    // Only skip hidden files if they are directories we are recursing into, 
                    // OR if we are looking at a file and the regex doesn't account for it (which it does).
                    // Actually, since the Regex handles the exclusion via (?![.]), we only need to protect recursion.
                    // Rule A: Skip hidden directories if Dot is false.
                    if (!_options.Dot && entry.Name.StartsWith('.')) continue;


                    // FIX: Skip symlink directories only if FollowSymlinks == false
                    // File symlinks are fine to list, but we don't recurse into dir symlinks.
                    // Rule B: Skip symlink directories if FollowSymlinks is false.
                    if (isSym && !_options.FollowSymlinks) continue;

                    stack.Push(entry.FullName);
                }
                else
                {
                    // File Logic
                    // Note: For files, let the basenameRegex fail if it doesn't match the dot.
                    if (basenameRegex.IsMatch(entry.Name))
                        yield return FormatResult(entry);
                }
            }
        }
    }

    public IEnumerable<string> Walk(string dir, string[] segments, int index, HashSet<string> seenDirs, bool directoryOnly)
    {
        // Fix loop detection
        string checkDir = dir;
        if (_options.FollowSymlinks) checkDir = PathUtils.ResolveSymlink(new DirectoryInfo(dir));
       
        if (seenDirs.Contains(checkDir)) yield break;
        seenDirs.Add(checkDir);

        IEnumerable<FileSystemInfo> entries;
        try
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) yield break;
			
			//dir found, can have entries
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
                    yield return FormatResult(new DirectoryInfo(dir));
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
                bool isSym = PathUtils.IsSymlink(entry);

                if (!isDir) continue;

                // FIX: Symlink recursion control
                if (isSym && !_options.FollowSymlinks) continue;

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
            bool isSym = PathUtils.IsSymlink(entry);

            if (!regex.IsMatch(entry.Name)) continue;

            // FIX: Allow file symlinks even if !FollowSymlinks. Only block recursion.
           
            if (isLast)
            {
                if (directoryOnly)
                {
                    if (isDir) yield return FormatResult(entry);
                }
                else
                {
                    // Include files, and include directories if requested
                    if (!isDir || _options.IncludeDirectories)
                        yield return FormatResult(entry);
                }
            }
            else if (isDir)
            {
                 // Recurse
                 if (isSym && !_options.FollowSymlinks) continue;
                 
                 foreach (var match in Walk(entry.FullName, segments, index + 1, seenDirs, directoryOnly))
                    yield return match;
            }
        }
    }
}
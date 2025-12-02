using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
//OS detection

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

                //main logic:
                var walker = new GlobWalker(walkRoot, new[] { relativePattern }, options, ignoreFilter);
                foreach (var f in walker.Execute(originalHasSep))
                {
                    yield return f;
                }
            }
        }
    }
}
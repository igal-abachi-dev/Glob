using System.Collections.Generic;

namespace Globbing;

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
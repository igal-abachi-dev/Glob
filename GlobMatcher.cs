using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Globbing;

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
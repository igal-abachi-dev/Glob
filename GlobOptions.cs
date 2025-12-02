using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Globbing;

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
using System;
using System.IO;
using System.Runtime.InteropServices;//OS detection

namespace Globbing;

internal static class PathUtils
{
    public static string NormalizePath(string path, bool windowsPathsNoEscape = true)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // FIX: Preserve escape backslashes if user wants escape semantics (like node-glob)
        // If WindowsPathsNoEscape is true (default), we treat '\' as separator -> '/'.
        // If false, we leave '\' alone so it can act as an escape char.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (windowsPathsNoEscape)
                return path.Replace('\\', '/');
           
            // If we are allowing escapes, we only normalize if the user used '/'
            // Users must use '/' as separators if they want to use '\' as escape.
            return path;
        }
       
        // On Linux/Mac, \ is a valid filename char, but usually treated as escape in globs.
        // We generally assume forward slash input, but some might pass backslashes hoping for normalization.
        // Standard glob behavior: always use / for separators.
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
        // Simple full path, does not resolve symlinks
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
   
    public static string ResolveSymlink(FileSystemInfo fileSystemInfo)
    {
#if NET6_0_OR_GREATER
        try
        {
            // Resolves symlinks to their final target
            var target = fileSystemInfo.ResolveLinkTarget(true);
            return target != null ? target.FullName : fileSystemInfo.FullName;
        }
        catch
        {
            return fileSystemInfo.FullName;
        }
#else
        // Fallback for older .NET: returning FullPath is the best we can do easily
        return fileSystemInfo.FullName;
#endif
    }

    public static bool IsSymlink(FileSystemInfo info)
    {
        if (info.LinkTarget != null) return true;
		// Fallback for older frameworks or specific reparse points
        return (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }
}
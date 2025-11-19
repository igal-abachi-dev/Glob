# Glob / Globbing for C# .NET

A high-performance, zero-dependency, cross-platform Glob matching library for .NET. (like node-glob)

This library provides a robust implementation of standard shell globbing patterns, allowing you to find files and directories using wildcards, brace expansion, and recursive directory walking. It is optimized to minimize disk I/O by leveraging `FileSystemInfo` attributes.

## Features

*   **Cross-Platform:** Works seamlessly on Windows (`\`) and Linux/macOS (`/`).
*   **Advanced Patterns:** Supports `**` (globstar), `?`, `*`, and character classes `[a-z]`.
*   **Brace Expansion:** Supports nested brace expansion (e.g., `src/{api,client}/v{1,2}/*.cs`).
*   **Negation:** Filter out results using `!` patterns (similar to `.gitignore`).
*   **Performance:** uses `DirectoryInfo` and caching to avoid redundant disk calls and handle symlink loops.
*   **Zero Dependencies:** Single namespace, pure C#.



## Quick Start

```csharp
using Globbing;

// 1. Basic Match
var files = Glob.Match("src/**/*.cs");

foreach (var file in files)
{
    Console.WriteLine(file);
}

// 2. Advanced Match with Options
var options = new GlobOptions
{
    BaseDirectory = "/var/www",
    IgnorePatterns = new[] { "**/bin", "**/obj", "**/node_modules" },
    CaseSensitive = false,
    Realpath = true // Returns absolute paths
};

var results = Glob.Match("**/*.{json,xml}", options);
```

## Supported Syntax

| Pattern | Description | Example |
| :--- | :--- | :--- |
| `*` | Matches any sequence of characters (excluding separators). | `src/*.cs` |
| `**` | Matches directories recursively. | `src/**/*.cs` |
| `?` | Matches a single character. | `img_?.png` |
| `[...]` | Matches a range or class of characters. | `file[0-9].txt` |
| `{a,b}` | **Brace Expansion**: Matches multiple options. | `*.{jpg,png}` |
| `!` | **Negation**: Excludes matches (used in IgnorePatterns). | `!secret.txt` |
| `/` | Path separators are normalized automatically. | `src/test/*.js` |

### Syntax Examples

*   `**/*.cs` - Match all `.cs` files in the current directory and all subdirectories.
*   `src/{app,test}/*.ts` - Match `.ts` files in `src/app` OR `src/test`.
*   `img_[0-9][0-9].png` - Match files like `img_01.png`, `img_99.png`.
*   `!**/node_modules/**` - Ignore everything inside any `node_modules` folder.

## API Reference

### `Glob.Match(string pattern, GlobOptions? options = null)`

The main entry point. Returns an `IEnumerable<string>` of matching paths.

*   **pattern**: The glob pattern string.
*   **options**: (Optional) Configuration settings.

### `GlobOptions` configuration

| Property | Type | Default | Description |
| :--- | :--- | :--- | :--- |
| `BaseDirectory` | `string` | `CWD` | The directory to start walking from. Defaults to `Directory.GetCurrentDirectory()`. |
| `IgnorePatterns` | `IEnumerable<string>` | `null` | A list of patterns to exclude (e.g., `new[] { "**/bin" }`). |
| `CaseSensitive` | `bool` | OS default | `true` on Linux, `false` on Windows. |
| `IncludeDirectories`| `bool` | `false` | If true, directories matching the pattern are returned in the results. |
| `FollowSymlinks` | `bool` | `false` | If true, the walker will follow directory symbolic links. |
| `ExpandBraces` | `bool` | `true` | Enables expansion of `{a,b}` syntax. |
| `Dot` | `bool` | `false` | If true, `*` will match file names starting with `.` (e.g. `.gitignore`). |
| `MatchBase` | `bool` | `false` | If true, a pattern without slashes (e.g. `*.js`) matches files in any subdirectory. |
| `Realpath` | `bool` | `false` | If true, returns the absolute system path instead of the relative path. |

## Handling `**` (Globstar)

The `**` pattern matches **directories**.
*   To find **all files** recursively, use: `**/*`
*   To find **specific extensions** recursively, use: `**/*.cs`
*   To find **directories** recursively, use: `**` (with `IncludeDirectories = true` implicitly or explicitly depending on context).

using System.Text;
using System.Text.RegularExpressions;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Core;

//Pattern matching relative to the project root; * and ? stay within one segment while ** crosses directories, and the segment before the wildcard limits the scan
internal static class PathPattern
{
    //Expand a pattern into matching file paths; build output is excluded since it belongs to a previous compile
    public static IEnumerable<string> Match(string root, string pattern)
    {
        var normalized = pattern.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (normalized.Length == 0)
            yield break;

        var wildcard = normalized.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            //Without a wildcard the pattern may point straight at a file
            var direct = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(direct))
            {
                if (!SourceFiles.IsGenerated(root, direct))
                    yield return direct;
                yield break;
            }
        }

        var expression = wildcard < 0 ? null : new Regex(Glob(normalized), RegexOptions.IgnoreCase);
        var slash = wildcard < 0 ? normalized.LastIndexOf('/') : normalized.LastIndexOf('/', wildcard);
        var baseDirectory = slash <= 0
            ? root
            : Path.Combine(root, normalized[..slash].Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(baseDirectory))
            yield break;

        foreach (var path in Directory.EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories))
        {
            if (SourceFiles.IsGenerated(root, path))
                continue;

            //A wildcard-free pattern pointing at a directory matches its whole subtree
            if (expression is null || expression.IsMatch(Relative(root, path)))
                yield return path;
        }
    }

    //Compile a pattern into a predicate over project relative paths using forward slashes
    //A pattern without a wildcard also covers everything under it, so a directory name alone excludes its whole subtree
    //An empty pattern matches nothing
    public static Func<string, bool> Compile(string pattern)
    {
        var normalized = pattern.Replace('\\', '/').TrimStart('/').TrimEnd('/');
        if (normalized.Length == 0)
            return static _ => false;

        if (normalized.IndexOfAny(['*', '?']) < 0)
        {
            var prefix = normalized + "/";
            return path => path.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        var expression = new Regex(Glob(normalized), RegexOptions.IgnoreCase);
        return path => expression.IsMatch(path);
    }

    //Translate a glob into a regex where ** crosses directories and * and ? stay in one segment
    private static string Glob(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                builder.Append(".*");
                index++;
            }
            else if (character == '*')
                builder.Append("[^/]*");
            else if (character == '?')
                builder.Append("[^/]");
            else
                builder.Append(Regex.Escape(character.ToString()));
        }

        return builder.Append('$').ToString();
    }

    //Path relative to the project root with forward slashes
    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');
}

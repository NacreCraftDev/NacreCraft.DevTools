using System.Collections.Concurrent;

namespace NacreCraft.ModBuild.Diagnostics;

//SourceTextCache keeps the text a file held when it was last read
//One check reads the same sources several times: the catalog pass, the Roslyn pass and the injection pass all walk them
//A file whose size and write time still match is served from memory, so the disk is touched once per run
internal static class SourceTextCache
{
    private sealed record Entry(long Length, DateTime WrittenUtc, string Text);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    //Read returns the file text, reusing what the last read stored when the file has not changed since
    //Null means the file could not be read, the caller decides whether that is worth a line of output
    public static string? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;

            if (Cache.TryGetValue(path, out var entry)
                && entry.Length == info.Length
                && entry.WrittenUtc == info.LastWriteTimeUtc)
            {
                return entry.Text;
            }

            var text = File.ReadAllText(path);

            //the stamp is taken after the read, so a write racing this one invalidates the entry instead of hiding it
            var after = new FileInfo(path);
            Cache[path] = new Entry(after.Exists ? after.Length : text.Length,
                after.Exists ? after.LastWriteTimeUtc : DateTime.MinValue, text);
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

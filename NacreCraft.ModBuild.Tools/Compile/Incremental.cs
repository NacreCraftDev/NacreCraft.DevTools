namespace NacreCraft.ModBuild.Compile;

//Incremental check modeled on MSBuild: compare input and output timestamps and skip the build when the output is not older than any input
//Like MSBuild it only sees timestamps, so it cannot catch misses caused by switching branches or changing the system clock
internal static class Incremental
{
    //Inputs must be listed generously because a missing entry silently turns into an up to date verdict
    public static bool IsUpToDate(string target, IEnumerable<string> inputs)
    {
        var output = new FileInfo(target);
        if (!output.Exists || output.Length == 0)
            return false;

        foreach (var path in inputs)
        {
            var input = new FileInfo(path);
            if (!input.Exists)
                continue;

            if (input.LastWriteTimeUtc > output.LastWriteTimeUtc)
                return false;
        }

        return true;
    }
}

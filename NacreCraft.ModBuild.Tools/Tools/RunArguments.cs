using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//Command line handling shared by the run tools
//The configured arguments and the debug flag come first and the user's last, so a repeated option wins with the user
internal static class RunArguments
{
    //ncm's own switch, which must not reach the kernel
    public const string RefreshOption = "--refresh";

    //Combine the configured arguments and the debug flag with the user's
    public static string[] Combine(string configured, bool debug, string[] args)
    {
        var combined = new List<string>(ArgumentLine.Split(configured));
        if (debug && !combined.Contains("--debug"))
            combined.Add("--debug");

        combined.AddRange(args);
        return combined.ToArray();
    }

    //Drop ncm's own switch before the kernel sees the arguments
    public static string[] WithoutRefresh(string[] args)
        => args.Where(arg => arg != RefreshOption).ToArray();

    //Append a kernel option unless the user already passed it, so their value stays in place
    public static string[] WithOption(string[] args, string name, string? value)
    {
        if (string.IsNullOrEmpty(value) || args.Contains(name))
            return args;

        return [.. args, name, value];
    }
}

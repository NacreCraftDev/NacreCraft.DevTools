using NacreCraft.Pit.Core;

namespace NacreCraft.Pit.Tools;

//Command line handling shared by the run tools
//The configured arguments and the debug flag come first and the user's last, so a repeated option wins with the user
internal static class RunArguments
{
    //pit's own switch, which must not reach the kernel
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

    //Drop the switches pit answers itself before the kernel sees the arguments
    //--plain belongs to the progress component, --refresh to the run tools
    public static string[] WithoutLocalSwitches(string[] args)
        => args.Where(arg => arg != RefreshOption && arg != Progress.PlainOption).ToArray();

    //Append a kernel option unless the user already passed it, so their value stays in place
    public static string[] WithOption(string[] args, string name, string? value)
    {
        if (string.IsNullOrEmpty(value) || args.Contains(name))
            return args;

        return [.. args, name, value];
    }
}

using NacreCraft.Pit.Core;

namespace NacreCraft.Pit.Experimental.Commands;

//SelfCheck reports what this experimental build actually loaded
//It is the skeleton's smoke test: if the versions, the tool count and the directory look right, the
//project reference and the shared registry are wired up, and a real experiment can be dropped in
internal static class SelfCheckCommand
{
    public static void Register()
        => ToolRegistry.Register("selfcheck", "Report the loaded versions, tool count and working directory", Run);

    private static int Run(string[] args)
    {
        var experimental = typeof(SelfCheckCommand).Assembly.GetName();
        var core = typeof(ToolRegistry).Assembly.GetName();

        Console.WriteLine($"experimental: {experimental.Name} {experimental.Version}");
        Console.WriteLine($"core: {core.Name} {core.Version}");
        Console.WriteLine($"tools: {ToolRegistry.All.Count}");
        Console.WriteLine($"directory: {Environment.CurrentDirectory}");
        return 0;
    }
}

using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools.Experimental;

//ToolFilter drops the stable tools that belong to the mod project lifecycle
//This build is for inspecting sources, so scaffolding, building, launching and updating are not offered here
internal static class ToolFilter
{
    private static readonly string[] Dropped =
    [
        "icon",
        "init",
        "new",
        "template",
        "add",
        "restore",
        "build",
        "clean",
        "hook",
        "runserver",
        "runclient",
        "upgrade",
        "update",
    ];

    public static void Apply()
    {
        foreach (var name in Dropped)
        {
            //a name that no longer exists only matters when maintaining this list, so it stays out of the console
            if (!ToolRegistry.Unregister(name))
                Trace.Log($"no registered tool named {name}");
        }
    }
}

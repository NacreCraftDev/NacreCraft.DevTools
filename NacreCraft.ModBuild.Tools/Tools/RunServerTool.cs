using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//RunServerTool runs the NacreCraft server against the current directory
//Runtime files come from the Server folder under the program root, while saves and logs land in run here
//It stages the current mod into run/mods first and passes the remaining arguments to the server untouched
internal static class RunServerTool
{
    public static void Register()
        => ToolRegistry.Register("runserver", "Build the current mod and run the NacreCraft server", Run,
        [
            new(RunArguments.RefreshOption, "Refresh the server cache against the remote index first, outside a mod project the refresh is all that runs"),
            new("[server args]", "Everything after runserver is passed to the server unchanged"),
        ]);

    //Run prepares the runtime files and the client jar, stages the mod and hands off to the launcher
    private static int Run(string[] args)
    {
        //The config is optional so the runtime files can be refreshed outside a mod project
        //It supplies the cache location, kernel source and jar version; when unreadable it warns and continues with defaults
        var config = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"warning: {configError}");
            Console.ResetColor();
        }

        ServerStore.Configure(config);
        ClientStore.Configure(config);

        //The kernel restarts the process once to get its native layer loaded and repeats this command line to do it
        //Everything was prepared before the first launch, and a repeat could not refresh the native layer this process
        //has mapped anyway, so the restarted process goes straight to the launch
        if (Launcher.Launched)
            return Launcher.Launch(Combine(config, args), LaunchTarget.Server, RuntimeSource());

        //--refresh is not a reason to skip the mod, a refreshed kernel running a stale mod is the combination this
        //command exists to prevent; it only changes how the runtime files are brought up to date
        var refresh = Array.IndexOf(args, RunArguments.RefreshOption) >= 0;
        if (refresh)
        {
            if (!ServerStore.Refresh())
                return 1;
        }
        else if (!ServerStore.Ensure())
        {
            return 1;
        }

        //Check for the run directory before staging, since staging creates it
        var firstRun = !Directory.Exists(Launcher.RunDirectory);
        var arguments = Combine(config, args);

        //--refresh stands on its own outside a mod project, where the runtime files are the whole point
        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
        {
            if (refresh)
                return Launcher.Launch(arguments, LaunchTarget.Server, RuntimeSource());

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: no {ModProject.ManifestName} in this directory or any parent");
            Console.ResetColor();
            return 1;
        }

        //The client jar comes from the predownload; the first launch uses it to extract the assets
        var jar = ClientStore.Ensure();
        if (jar is null)
            return 1;

        //Build and deploy both work from the project root, so nothing can launch outside a mod project
        var root = Path.GetDirectoryName(project.ManifestPath)!;
        if (!ModStaging.Stage(root, Launcher.RunDirectory,
            config?.Build.DependsOnModApi ?? NcBuild.DefaultDependsOnModApi))
            return 1;

        //The assets stay in run after being extracted once, so the jar is only pointed at on the first launch
        var prepared = firstRun ? RunArguments.WithOption(arguments, "--jar-path", jar) : arguments;
        return Launcher.Launch(prepared, LaunchTarget.Server, RuntimeSource());
    }

    //RuntimeSource the directory the launcher loads from, with the shared files merged under the server ones
    //The restarted process skips the store, so the merge is asked for again here, which is a stamp check when nothing changed
    private static string RuntimeSource()
        => ServerStore.UsesLocalDirectory ? ServerStore.Root : RuntimeStore.Prepare(RuntimeSide.Server);

    //Combine the server section of the config with the user's arguments
    private static string[] Combine(NcProject? config, string[] args)
        => RunArguments.Combine(config?.Server.Args ?? string.Empty, config?.Server.Debug == true,
            RunArguments.WithoutRefresh(args));
}

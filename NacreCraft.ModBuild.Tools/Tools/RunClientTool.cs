using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//RunClientTool runs the NacreCraft client against the current directory
//The flow matches runserver and shares its run folder, except the client also needs the resource objects the jar
//cannot supply: the language tables, the fonts and every sound
internal static class RunClientTool
{
    public static void Register()
        => ToolRegistry.Register("runclient", "Build the current mod and run the NacreCraft client", Run,
        [
            new(RunArguments.RefreshOption, "Refresh the kernel cache against the remote index first, outside a mod project the refresh is all that runs"),
            new("[client args]", "Everything after runclient is passed to the client unchanged"),
        ]);

    //Run prepares the kernel, the client jar and the shared resources, stages the mod and hands off to the launcher
    private static int Run(string[] args)
    {
        var config = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"warning: {configError}");
            Console.ResetColor();
        }

        ServerStore.Configure(config);
        ClientStore.Configure(config);

        //The kernel restarts once to map its native layer and repeats this command line, so the repeat skips straight to the launch
        //The restart runs inside run, where the project cannot be found, but the resource locations are absolute, so they are
        //named again here; without them the process that actually runs the game would load no resource object at all
        if (Launcher.Launched)
            return Launcher.Launch(WithResources(Combine(config, args)), LaunchTarget.Client, RuntimeSource());

        var refresh = Array.IndexOf(args, RunArguments.RefreshOption) >= 0;
        if (refresh)
        {
            //the client needs the shared files plus its own half, not the server ones
            if (!RuntimeStore.Refresh(RuntimeSide.Client))
                return 1;
        }
        else if (!RuntimeStore.Ensure(RuntimeSide.Client))
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
                return Launcher.Launch(WithResources(arguments), LaunchTarget.Client, RuntimeSource());

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: no {ModProject.ManifestName} in this directory or any parent");
            Console.ResetColor();
            return 1;
        }

        //The jar carries most of the resources while the index-driven objects fill in what it leaves out
        var jar = ClientStore.Ensure();
        if (jar is null)
            return 1;

        //The objects are shared between every project, so only the missing ones are fetched
        if (AssetsStore.Ensure() is null)
            return 1;

        //Sounds are not among what the kernel lands from the index, so they are handed over as a directory instead
        var sounds = AssetsStore.EnsureSounds();

        var root = Path.GetDirectoryName(project.ManifestPath)!;
        if (!ModStaging.Stage(root, Launcher.RunDirectory,
            config?.Build.DependsOnModApi ?? NcBuild.DefaultDependsOnModApi))
            return 1;

        var prepared = WithResources(arguments, AssetsStore.Root, AssetsStore.IndexId, sounds);
        //The jar is only pointed at on the first launch, since the extracted assets stay in run afterwards
        if (firstRun)
            prepared = RunArguments.WithOption(prepared, "--jar-path", jar);

        return Launcher.Launch(prepared, LaunchTarget.Client, RuntimeSource());
    }

    //RuntimeSource the directory the launcher loads from, with the shared files merged under the client ones
    //The restarted process skips the store, so the merge is asked for again here, which is a stamp check when nothing changed
    private static string RuntimeSource()
        => ServerStore.UsesLocalDirectory ? ServerStore.Root : RuntimeStore.Prepare(RuntimeSide.Client);

    //WithResources names the cache locations as they are on disk, for a launch whose stores were not prepared here
    private static string[] WithResources(string[] args)
        => WithResources(args, AssetsStore.Root, AssetsStore.CachedIndex(), AssetsStore.SoundsRoot);

    //WithResources points the client at the shared objects, the index naming them and the sound copy
    //The jar supplies the rest of the resources, and the kernel lands whatever the index lists
    private static string[] WithResources(string[] args, string assetsDir, string indexId, string? soundsDir)
    {
        var prepared = RunArguments.WithOption(args, "--assetsDir", assetsDir);
        prepared = RunArguments.WithOption(prepared, "--assetIndex", indexId);
        return RunArguments.WithOption(prepared, "--sounds-dir", soundsDir);
    }

    //Combine the client section of the config with the user's arguments
    private static string[] Combine(NcProject? config, string[] args)
        => RunArguments.Combine(config?.Client.Args ?? string.Empty, false, RunArguments.WithoutRefresh(args));
}

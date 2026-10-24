using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//ModStaging stages the mods to load into the run directory
//It moves the modapi from Build/kernel, builds the current project and lands both in run/mods so the server scans them on startup
internal static class ModStaging
{
    //ModApiFileName the modapi file name inside the kernel
    //Mod dlls reference it but neither the kernel's embedded libraries nor the mod's embedded dependencies carry this assembly, so a copy must be staged in mods
    private const string ModApiFileName = "NacreCraft.ModApi.dll";

    //ModsDirectoryName the mods directory name, the one the loader scans
    private const string ModsDirectoryName = "mods";

    //Stage prepares the mods directory and reports whether startup can continue
    //Any failing step returns false, the server should not start without staged mods
    public static bool Stage(string projectRoot, string runDirectory, bool stageModApi = true)
    {
        var mods = Path.Combine(runDirectory, ModsDirectoryName);
        Directory.CreateDirectory(mods);

        if (stageModApi)
            StageModApi(projectRoot, mods);

        //Diagnose and build, collect the output into the project's Build and move it all into mods
        if (BuildTool.Run([]) != 0)
            return false;

        return StageBuildOutput(projectRoot, mods);
    }

    //StageModApi moves the modapi from Build/kernel into mods
    private static void StageModApi(string projectRoot, string mods)
    {
        var source = Path.Combine(projectRoot, ProjectLayout.Kernel, ModApiFileName);
        if (!File.Exists(source))
        {
            Trace.Log($"no {Path.Combine(ProjectLayout.Kernel, ModApiFileName)} under {projectRoot}");
            return;
        }

        File.Copy(source, Path.Combine(mods, ModApiFileName), overwrite: true);
        Console.WriteLine($"Staged {ModApiFileName} to {mods}");
    }

    //StageBuildOutput moves the mod output into mods
    //The build output also holds copies of the kernel reference assemblies which carry no manifest and would only be skipped by the loader
    //So only the ones with ncmod.json are moved, keeping kernel components out of mods
    private static bool StageBuildOutput(string projectRoot, string mods)
    {
        var output = BuildTool.OutputPath(projectRoot);
        if (!Directory.Exists(output))
        {
            Console.WriteLine($"error: no build output at {output}");
            return false;
        }

        var copied = 0;
        foreach (var file in Directory.EnumerateFiles(output, "*.dll"))
        {
            if (!ModAssembly.CarriesManifest(file))
                continue;

            File.Copy(file, Path.Combine(mods, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        Console.WriteLine($"Staged {copied} mod assembly(ies) to {mods}");
        return true;
    }
}

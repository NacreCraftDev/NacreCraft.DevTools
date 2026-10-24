using System.Diagnostics;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using NacreCraft.ModBuild.Tools;

//System.Diagnostics also defines a Trace, so name the project's own
using Trace = NacreCraft.ModBuild.Core.Trace;

namespace NacreCraft.ModBuild.Compile;

//Compile references for plain csproj projects, obtained by running msbuild's ResolveReferences instead of parsing the xml ourselves
//Assembly name resolution, package restore and project references are msbuild and nuget's job and reimplementing them only misses cases
//ncproj projects do not come through here, their references come from the config and Build directory
internal static class CsprojReferences
{
    //One resolve per project is enough since check and build both ask for it
    private static readonly Dictionary<string, IReadOnlyList<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    //Empty when there is no csproj or the engine is unavailable, which leaves the caller with framework references only
    public static IReadOnlyList<string> Assemblies(string root)
    {
        var project = Directory.EnumerateFiles(root, "*.csproj").FirstOrDefault();
        if (project is null)
            return [];

        if (Cache.TryGetValue(project, out var cached))
            return cached;

        var resolved = Resolve(project);
        Cache[project] = resolved;
        return resolved;
    }

    //Runs ResolveReferences once and collects the ReferencePath items
    private static IReadOnlyList<string> Resolve(string project)
    {
        if (!MsBuildLibrary.Attach(out var engineError))
        {
            Console.WriteLine($"warning: {engineError}, the references of {Path.GetFileName(project)} are skipped");
            return [];
        }

        var directory = Path.GetDirectoryName(project)!;
        //Restore first when the assets file is missing, otherwise ResolvePackageAssets fails outright
        if (!File.Exists(Path.Combine(directory, "obj", "project.assets.json")) && !Restore(directory))
            return [];

        try
        {
            TargetRunner.Sdks();
            var collection = new ProjectCollection();
            var loaded = new Project(project, globalProperties: null, toolsVersion: null, projectCollection: collection);
            //ReferencePath is only populated by a target run, so the instance is needed instead of the evaluated project
            var instance = loaded.CreateProjectInstance();
            var logger = new ErrorLogger();
            if (!instance.Build("ResolveReferences", [logger]))
            {
                foreach (var message in logger.Errors.Take(8))
                    Console.WriteLine($"error: {message}");

                if (logger.Errors.Count == 0)
                    Console.WriteLine($"error: ResolveReferences failed for {Path.GetFileName(project)}");

                Trace.Log($"ResolveReferences failed for {project}");
                return [];
            }

            var paths = new List<string>();
            foreach (var item in instance.GetItems("ReferencePath"))
            {
                if (File.Exists(item.EvaluatedInclude))
                    paths.Add(item.EvaluatedInclude);
            }

            Trace.Log($"csproj {project} resolved {paths.Count} reference(s)");
            return paths;
        }
        catch (Exception e)
        {
            Trace.Log($"cannot resolve the references of {project}: {e.GetType().Name}: {e.Message}");
            return [];
        }
    }

    //ErrorLogger keeps the errors of a reference resolve and drops every warning, so a referenced project's compile
    //warnings never spill into the caller's output
    private sealed class ErrorLogger : ILogger
    {
        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;

        public string? Parameters { get; set; }

        public List<string> Errors { get; } = new();

        public void Initialize(IEventSource eventSource)
            => eventSource.ErrorRaised += (_, e) =>
                Errors.Add($"{e.File}({e.LineNumber},{e.ColumnNumber}): {e.Code} {e.Message}");

        public void Shutdown()
        {
        }
    }

    //Restores through dotnet, which also prepares the output of referenced projects
    private static bool Restore(string directory)
    {
        Console.WriteLine($"Restoring {new DirectoryInfo(directory).Name} before resolving its references");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
        };
        //Keep child process output in English regardless of the system language
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.WriteLine("error: could not start dotnet");
            return false;
        }

        process.WaitForExit();
        if (process.ExitCode == 0)
            return true;

        Console.WriteLine($"error: dotnet restore exited with code {process.ExitCode}");
        return false;
    }
}

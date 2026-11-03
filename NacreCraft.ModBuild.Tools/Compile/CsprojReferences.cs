using System.Diagnostics;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
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
    //A referenced project that was never built has no output to compile against, so a missing one makes this build the
    //referenced projects first; configuration picks which output directory both that build and the resolve use
    //progress names the project being resolved, since one resolve per project is what makes a solution root slow
    public static IReadOnlyList<string> Assemblies(string root, string configuration, Action<string>? progress = null)
    {
        var projects = Projects(root);
        if (projects.Count == 0)
            return [];

        if (projects.Count == 1)
        {
            progress?.Invoke($"references: {Path.GetFileName(projects[0])}");
            return Cached(projects[0], configuration);
        }

        //a solution root holds one project per directory, and the compilation already carries every source file,
        //so their own outputs are left out and only the references from outside the solution are merged in
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            progress?.Invoke($"references: {Path.GetFileName(project)}");
            foreach (var path in Cached(project, configuration))
            {
                if (!IsOwnOutput(root, path) && seen.Add(path))
                    merged.Add(path);
            }
        }

        return merged;
    }

    //Projects lists the csproj files of the project at root, descending only when the root itself has none
    private static List<string> Projects(string root)
    {
        var top = Directory.EnumerateFiles(root, "*.csproj").ToList();
        if (top.Count > 0)
            return top;

        return Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildPath(root, path))
            .ToList();
    }

    //PathOf the single csproj at root, null when there is none or the root holds several
    internal static string? PathOf(string root)
    {
        var projects = Projects(root);
        return projects.Count == 1 ? projects[0] : null;
    }

    //Cached resolves one project once per run, since check and build both ask for the same references
    private static IReadOnlyList<string> Cached(string project, string configuration)
    {
        var key = project + "|" + configuration;
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var resolved = Resolve(project, configuration);
        Cache[key] = resolved;
        return resolved;
    }

    //IsBuildPath whether a csproj sits under an output folder that must not contribute references
    private static bool IsBuildPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || part.Equals("Build", StringComparison.OrdinalIgnoreCase));
    }

    //IsOwnOutput whether a reference is one of the solution's own build products, which the source scan already covers
    private static bool IsOwnOutput(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return false;

        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    //Runs ResolveReferences once and collects the ReferencePath items
    private static IReadOnlyList<string> Resolve(string project, string configuration)
    {
        if (!MsBuildLibrary.Attach(out var engineError))
        {
            Progress.WriteLine($"warning: {engineError}, the references of {Path.GetFileName(project)} are skipped");
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
            //Design time properties keep the resolve from walking into compilation and deps file generation, which
            //would need the compiler and would build the whole project just to learn its references
            var globals = new Dictionary<string, string>
            {
                //the resolve has to look at the same output directory the later build writes to
                ["Configuration"] = configuration,
                ["DesignTimeBuild"] = "true",
                ["SkipCompilerExecution"] = "true",
                ["ProvideCommandLineArgs"] = "true",
                ["BuildProjectReferences"] = "false",
            };
            var loaded = new Project(project, globals, toolsVersion: null, projectCollection: collection);
            //ReferencePath is only populated by a target run, so the instance is needed instead of the evaluated project
            var instance = loaded.CreateProjectInstance();
            var logger = new ErrorLogger();
            if (!instance.Build("ResolveReferences", [logger]))
            {
                foreach (var message in logger.Errors.Take(8))
                    Progress.WriteLine($"error: {message}");

                if (logger.Errors.Count == 0)
                    Progress.WriteLine($"error: ResolveReferences failed for {Path.GetFileName(project)}");

                Trace.Log($"ResolveReferences failed for {project}");
                return [];
            }

            var paths = new List<string>();
            var missing = new List<string>();
            foreach (var item in instance.GetItems("ReferencePath"))
            {
                if (File.Exists(item.EvaluatedInclude))
                    paths.Add(item.EvaluatedInclude);
                else
                    missing.Add(item.EvaluatedInclude);
            }

            //a referenced project that was never built has no output to compile against, so it is built first
            //its own dependencies come along, which makes the direct project references enough to restore the whole chain
            if (missing.Count > 0)
            {
                BuildReferences(instance, configuration);
                foreach (var path in missing)
                {
                    if (File.Exists(path))
                        paths.Add(path);
                    else
                        Trace.Log($"missing reference {path}");
                }
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

    //Builds the projects this one references so their outputs land where the resolve already pointed
    //Dotnet builds the chain in the right order, and the engine is left out of it since a design time resolve does not compile
    private static void BuildReferences(ProjectInstance instance, string configuration)
    {
        foreach (var item in instance.GetItems("ProjectReference"))
        {
            var project = item.GetMetadataValue("FullPath");
            if (project.Length == 0 || !File.Exists(project))
                continue;

            Progress.Detail($"building {Path.GetFileNameWithoutExtension(project)}");
            var directory = Path.GetDirectoryName(project)!;
            if (!DotnetRunner.Run(directory, ["build", project, "--nologo", "-v", "q", "-c", configuration],
                    live: false, Progress.Break, out var exitCode))
                Progress.WriteLine($"warning: dotnet build exited with code {exitCode} for {Path.GetFileName(project)}");
        }
    }

    //Restores through dotnet, which also prepares the output of referenced projects
    private static bool Restore(string directory)
    {
        Progress.WriteLine($"Restoring {new DirectoryInfo(directory).Name} before resolving its references");
        if (!DotnetRunner.Run(directory, ["restore", "--nologo"], Progress.Plain, Progress.Break,
                out var exitCode))
        {
            Progress.WriteLine($"error: dotnet restore exited with code {exitCode}");
            return false;
        }

        return true;
    }
}

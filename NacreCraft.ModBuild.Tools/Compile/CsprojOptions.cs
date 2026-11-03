using Microsoft.Build.Evaluation;
using Microsoft.CodeAnalysis;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Tools;

namespace NacreCraft.ModBuild.Compile;

//Compile settings of a plain csproj, read from the project instead of guessed
//The check has to compile the same branch the real build does: a switch the project sets, such as AllowUnsafeBlocks,
//or a define that removes code, would otherwise turn a working file into an error
internal static class CsprojOptions
{
    private static readonly Dictionary<string, CompileOptions> Cache = new(StringComparer.OrdinalIgnoreCase);

    //Of the options for the project at root, falling back to the defaults when the project cannot be evaluated
    public static CompileOptions Of(string root, string configuration, bool lint, bool taint)
    {
        var path = CsprojReferences.PathOf(root);
        if (path is null)
            return CompileOptions.Default with { Lint = lint, Taint = taint };

        var key = path + "|" + configuration;
        if (!Cache.TryGetValue(key, out var options))
        {
            options = Read(path, configuration) ?? CompileOptions.Default with { Lint = false, Taint = false };
            Cache[key] = options;
        }

        return options with { Lint = lint, Taint = taint };
    }

    private static CompileOptions? Read(string path, string configuration)
    {
        if (!MsBuildLibrary.Attach(out _))
            return null;

        try
        {
            TargetRunner.Sdks();
            var globals = new Dictionary<string, string> { ["Configuration"] = configuration };
            var project = new Project(path, globals, toolsVersion: null, new ProjectCollection());
            return From(project, path);
        }
        catch (Exception e)
        {
            Trace.Log($"cannot read the compile settings of {path}: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static CompileOptions From(Project project, string path)
    {
        var name = Property(project, "AssemblyName");
        if (name.Length == 0)
            name = Path.GetFileNameWithoutExtension(path);

        var output = Property(project, "OutputType");
        var kind = string.Equals(output, "Exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(output, "WinExe", StringComparison.OrdinalIgnoreCase)
                ? OutputKind.ConsoleApplication
                : OutputKind.DynamicallyLinkedLibrary;

        return new CompileOptions(
            name,
            Or(project, "LangVersion", NcCheck.DefaultLangVersion),
            NullableOf(project),
            ImplicitUsingsOf(project),
            Property(project, "DefineConstants"),
            kind,
            Flag(project, "AllowUnsafeBlocks", NcBuild.DefaultAllowUnsafeBlocks),
            Flag(project, "Optimize", NcBuild.DefaultOptimize),
            Flag(project, "CheckForOverflowUnderflow", false),
            Or(project, "PlatformTarget", NcBuild.DefaultPlatformTarget),
            Number(project, "WarningLevel", NcBuild.DefaultWarningLevel),
            Property(project, "NoWarn"),
            Property(project, "WarningsAsErrors"),
            Property(project, "WarningsNotAsErrors"),
            Flag(project, "TreatWarningsAsErrors", NcBuild.DefaultTreatWarningsAsErrors),
            Flag(project, "Deterministic", NcBuild.DefaultDeterministic),
            Property(project, "PathMap"),
            Property(project, "Features"),
            Flag(project, "EnableNETAnalyzers", NcBuild.DefaultEnableNETAnalyzers),
            Or(project, "AnalysisLevel", NcBuild.DefaultAnalysisLevel),
            Flag(project, "EnforceCodeStyleInBuild", false),
            Flag(project, "GenerateDocumentationFile", NcBuild.DefaultGenerateDocumentationFile),
            Flag(project, "EmitCompilerGeneratedFiles", NcBuild.DefaultEmitCompilerGeneratedFiles),
            Property(project, "CompilerGeneratedFilesOutputPath"),
            false,
            false);
    }

    //Nullable is a mode rather than a flag, only disable turns the annotations off
    private static bool NullableOf(Project project)
    {
        var value = Property(project, "Nullable");
        return value.Length == 0 || !string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ImplicitUsingsOf(Project project)
    {
        var value = Property(project, "ImplicitUsings");
        if (value.Length == 0)
            return CompileOptions.Default.ImplicitUsings;

        return string.Equals(value, "enable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Property(Project project, string name) => project.GetPropertyValue(name).Trim();

    private static string Or(Project project, string name, string fallback)
    {
        var value = Property(project, name);
        return value.Length == 0 ? fallback : value;
    }

    private static bool Flag(Project project, string name, bool fallback)
        => bool.TryParse(Property(project, name), out var value) ? value : fallback;

    private static int Number(Project project, string name, int fallback)
        => int.TryParse(Property(project, name), out var value) ? value : fallback;
}

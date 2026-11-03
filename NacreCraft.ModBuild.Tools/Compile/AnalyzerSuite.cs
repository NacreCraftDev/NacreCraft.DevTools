using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Tools;

namespace NacreCraft.ModBuild.Compile;

//Supplies the analyzers the sdk ships and the severities its analysis level defines
//Both live beside the sdk targets, so the very files a dotnet build reads are used here instead of a copy
internal static class AnalyzerSuite
{
    //Folder holding the sdk's own analyzers, relative to the engine directory
    private static readonly string AnalyzerFolder = Path.Combine("Sdks", "Microsoft.NET.Sdk", "analyzers");

    //Folder holding one globalconfig per analysis level
    private static readonly string ConfigFolder = Path.Combine(AnalyzerFolder, "build", "config");

    //Assemblies the sdk ships for csharp projects
    private static readonly string[] AssemblyNames =
    [
        "Microsoft.CodeAnalysis.NetAnalyzers.dll",
        "Microsoft.CodeAnalysis.CSharp.NetAnalyzers.dll",
    ];

    private static readonly Dictionary<string, ReportDiagnostic> Empty = new(StringComparer.OrdinalIgnoreCase);

    //SeveritiesOf reads the rule set the analysis level stands for, empty when the analyzers are off
    //A level that resolves to no file only loses the preset, the analyzers still run with their own defaults
    public static IReadOnlyDictionary<string, ReportDiagnostic> SeveritiesOf(CompileOptions options)
        => options.EnableNETAnalyzers ? ReadSeverities(ConfigOf(options.AnalysisLevel)) : Empty;

    //AnalyzersOf loads every diagnostic analyzer reachable from the sdk and from the declared packages
    //A package brings its own rules along, and one that fails to load costs only that assembly
    public static IReadOnlyList<DiagnosticAnalyzer> AnalyzersOf(string root, CompileOptions options)
    {
        var analyzers = new List<DiagnosticAnalyzer>();
        foreach (var path in AssembliesOf(root, options))
            Load(path, analyzers);

        return analyzers;
    }

    private static IEnumerable<string> AssembliesOf(string root, CompileOptions options)
    {
        if (options.EnableNETAnalyzers && MsBuildLibrary.SdkDirectory is { } sdk)
        {
            foreach (var name in AssemblyNames)
            {
                var path = Path.Combine(sdk, AnalyzerFolder, name);
                if (File.Exists(path))
                    yield return path;
            }
        }

        foreach (var path in PackageResolver.Analyzers(root))
            yield return path;
    }

    //Load picks the analyzers out of an assembly; a source generator is not one, so it is skipped here
    private static void Load(string path, List<DiagnosticAnalyzer> analyzers)
    {
        try
        {
            AddTypes(Assembly.LoadFrom(path), analyzers);
        }
        catch (ReflectionTypeLoadException e)
        {
            //an analyzer built against another roslyn version still resolves its types once the loaded one is reused
            Trace.Log($"cannot load some types from {path}: {e.LoaderExceptions.FirstOrDefault()?.Message}");
            foreach (var type in e.Types)
                Add(type, analyzers);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or FileLoadException
                                      or TypeLoadException)
        {
            Trace.Log($"cannot load analyzers from {path}: {e.Message}");
        }
    }

    //Add instantiates every analyzer type in the assembly that can be created without arguments
    private static void Add(Type? type, List<DiagnosticAnalyzer> analyzers)
    {
        if (type is null || type.IsAbstract || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            return;

        if (type.GetConstructor(Type.EmptyTypes) is null)
            return;

        try
        {
            if (Activator.CreateInstance(type) is DiagnosticAnalyzer analyzer)
                analyzers.Add(analyzer);
        }
        catch (Exception e) when (e is TypeLoadException or MissingMethodException or InvalidOperationException)
        {
            Trace.Log($"cannot create analyzer {type.FullName}: {e.Message}");
        }
    }

    private static void AddTypes(Assembly assembly, List<DiagnosticAnalyzer> analyzers)
    {
        foreach (var type in assembly.GetTypes())
            Add(type, analyzers);
    }

    //ConfigOf resolves the globalconfig the analysis level names
    //The value carries an optional mode after a dash, as in latest-all or 10.0-minimum
    private static string? ConfigOf(string level)
    {
        if (MsBuildLibrary.SdkDirectory is not { } sdk)
            return null;

        var folder = Path.Combine(sdk, ConfigFolder);
        if (!Directory.Exists(folder))
            return null;

        var parts = level.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var version = parts.Length > 0 ? parts[0] : string.Empty;
        var mode = parts.Length > 1 ? parts[1].ToLowerInvariant() : "default";

        //latest follows the newest level this sdk ships, the same way the sdk itself resolves it
        if (string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase))
            version = Newest(folder);

        if (version.Length == 0)
            return null;

        //10.0 and 10 both name the same file, which is keyed by major version only
        version = version.Split('.')[0];
        var path = Path.Combine(folder, $"analysislevel_{version}_{mode}.globalconfig");
        if (File.Exists(path))
            return path;

        Trace.Log($"no analysis level file for {level}, the analyzers use their own defaults");
        return null;
    }

    //Newest is the highest major version with a default config, empty when the folder holds none
    private static string Newest(string folder)
        => Directory.EnumerateFiles(folder, "analysislevel_*_default.globalconfig")
            .Select(path => Path.GetFileName(path).Split('_'))
            .Where(parts => parts.Length >= 3 && int.TryParse(parts[1], out _))
            .OrderByDescending(parts => int.Parse(parts[1]))
            .Select(parts => parts[1])
            .FirstOrDefault() ?? string.Empty;

    //ReadSeverities turns the globalconfig entries into per rule settings
    private static IReadOnlyDictionary<string, ReportDiagnostic> ReadSeverities(string? path)
    {
        if (path is null)
            return Empty;

        var severities = new Dictionary<string, ReportDiagnostic>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var text = line.Trim();
                if (!text.StartsWith("dotnet_diagnostic.", StringComparison.Ordinal))
                    continue;

                var split = text.IndexOf('=');
                if (split <= 0)
                    continue;

                //the key is dotnet_diagnostic.<id>.<setting>
                var key = text["dotnet_diagnostic.".Length..split].Trim();
                var dot = key.LastIndexOf('.');
                if (dot <= 0 || !string.Equals(key[(dot + 1)..], "severity", StringComparison.OrdinalIgnoreCase))
                    continue;

                severities[key[..dot]] = SeverityOf(text[(split + 1)..].Trim());
            }
        }
        catch (IOException e)
        {
            Trace.Log($"cannot read {path}: {e.Message}");
        }

        return severities;
    }

    //SeverityOf maps the editorconfig spelling onto roslyn's report kind
    private static ReportDiagnostic SeverityOf(string value)
        => value.ToLowerInvariant() switch
        {
            "error" => ReportDiagnostic.Error,
            "warning" => ReportDiagnostic.Warn,
            "suggestion" => ReportDiagnostic.Info,
            "silent" => ReportDiagnostic.Hidden,
            "none" => ReportDiagnostic.Suppress,
            _ => ReportDiagnostic.Default,
        };
}

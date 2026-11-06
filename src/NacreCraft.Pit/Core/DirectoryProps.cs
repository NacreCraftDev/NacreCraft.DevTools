using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NacreCraft.Pit.Core;

//Settings shared by every project below a directory, the ncproj counterpart of Directory.Build.props
//The files are collected from the top of the tree down, so a nearer file wins a conflict; a value the project declares
//itself still wins over every file, and the warning id lists accumulate from the outermost file to the project
internal sealed class DirectoryProps
{
    //Name searched upward from a project directory
    public const string FileName = "Directory.NcProject.yml";

    //The file the values below were read from, used in the warnings
    public string Path { get; private set; } = string.Empty;

    //<check> section
    public string? LangVersion { get; private set; }

    //<build> section, null meaning the file leaves that setting alone
    public string? AssemblyName { get; private set; }
    public string? Configuration { get; private set; }
    public string? Output { get; private set; }
    public string? OutputType { get; private set; }
    public string? ExtraArgs { get; private set; }
    public string? DefineConstants { get; private set; }
    public string? RootNamespace { get; private set; }
    public string? PlatformTarget { get; private set; }
    public string? PathMap { get; private set; }
    public string? Features { get; private set; }
    public string? AnalysisLevel { get; private set; }
    public string? DebugType { get; private set; }
    public string? DocumentationFile { get; private set; }
    public string? CompilerGeneratedFilesOutputPath { get; private set; }
    public bool? Nullable { get; private set; }
    public bool? ImplicitUsings { get; private set; }
    public bool? DependsOnModApi { get; private set; }
    public bool? AllowUnsafeBlocks { get; private set; }
    public bool? SharedDependencies { get; private set; }
    public bool? Optimize { get; private set; }
    public bool? CheckForOverflowUnderflow { get; private set; }
    public bool? TreatWarningsAsErrors { get; private set; }
    public bool? Deterministic { get; private set; }
    public bool? EnableNETAnalyzers { get; private set; }
    public bool? EnforceCodeStyleInBuild { get; private set; }
    public bool? DebugSymbols { get; private set; }
    public bool? GenerateDocumentationFile { get; private set; }
    public bool? EmitCompilerGeneratedFiles { get; private set; }
    public int? WarningLevel { get; private set; }
    public IReadOnlyList<string>? NoWarn { get; private set; }
    public IReadOnlyList<string>? WarningsAsErrors { get; private set; }
    public IReadOnlyList<string>? WarningsNotAsErrors { get; private set; }

    //Whether the file carries any build setting, so the caller knows the section was touched
    public bool TouchesBuild
        => AssemblyName is not null || Configuration is not null || Output is not null || OutputType is not null
            || ExtraArgs is not null || DefineConstants is not null || RootNamespace is not null
            || PlatformTarget is not null || PathMap is not null || Features is not null || AnalysisLevel is not null
            || DebugType is not null || DocumentationFile is not null || CompilerGeneratedFilesOutputPath is not null
            || Nullable is not null || ImplicitUsings is not null || DependsOnModApi is not null
            || AllowUnsafeBlocks is not null || SharedDependencies is not null || Optimize is not null
            || CheckForOverflowUnderflow is not null || TreatWarningsAsErrors is not null || Deterministic is not null
            || EnableNETAnalyzers is not null || EnforceCodeStyleInBuild is not null || DebugSymbols is not null
            || GenerateDocumentationFile is not null || EmitCompilerGeneratedFiles is not null
            || WarningLevel is not null || NoWarn is not null || WarningsAsErrors is not null
            || WarningsNotAsErrors is not null;

    public bool TouchesCheck => LangVersion is not null;

    //Collect every props file above a directory, outermost first so the near ones apply last
    public static IReadOnlyList<DirectoryProps> Collect(string directory)
    {
        var found = new List<DirectoryProps>();
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var path = System.IO.Path.Combine(current.FullName, FileName);
            if (!File.Exists(path))
                continue;

            var props = Read(path);
            if (props is not null)
                found.Add(props);
        }

        found.Reverse();
        return found;
    }

    //ApplyAll folds the files over a build section
    //present names the attributes the project writes itself, which are never replaced; when the project writes none the
    //whole section comes from the files, layered over the defaults it already carried
    public static NcBuild ApplyAll(NcBuild build, IReadOnlyList<DirectoryProps> layers, IReadOnlySet<string> present)
    {
        foreach (var layer in layers)
            build = build.With(layer, present);

        return build;
    }

    //ApplyAll the same for the check section, which only carries the language version
    public static NcCheck ApplyAll(NcCheck check, IReadOnlyList<DirectoryProps> layers, IReadOnlySet<string> present)
    {
        var langVersion = check.LangVersion;
        foreach (var layer in layers)
        {
            if (layer.LangVersion is { Length: > 0 } value && !present.Contains("LangVersion"))
                langVersion = value;
        }

        return new NcCheck(langVersion);
    }

    //MergeWarningIds puts the directory ids in front of the project ones, keeping the first copy of a repeated id
    //A directory can therefore silence a rule for every project below it and a project can still add its own
    internal static string MergeIds(IReadOnlyList<string>? layer, string current)
    {
        if (layer is not { Count: > 0 })
            return current;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new List<string>();
        foreach (var id in layer.Concat(Split(current)))
        {
            if (seen.Add(id))
                ids.Add(id);
        }

        return string.Join(';', ids);
    }

    private static IEnumerable<string> Split(string value)
        => value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static DirectoryProps? Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException e)
        {
            Warn($"{path} cannot be read: {e.Message}");
            return null;
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(text));
        }
        catch (YamlException e)
        {
            Warn($"{path} is not valid yaml: {e.Message}");
            return null;
        }

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            Warn($"{path} must hold a mapping with a check or build section");
            return null;
        }

        var props = new DirectoryProps { Path = path };
        foreach (var (key, value) in Members(root))
        {
            if (value is not YamlMappingNode section)
            {
                Warn($"{path}: {key} must be a mapping");
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "check":
                    props.ReadCheck(section, path);
                    break;
                case "build":
                    props.ReadBuild(section, path);
                    break;
                default:
                    Warn($"{path}: unknown section {key}, known ones are check and build");
                    break;
            }
        }

        return props;
    }

    private void ReadCheck(YamlMappingNode section, string path)
    {
        foreach (var (key, value) in Members(section))
        {
            switch (key.ToLowerInvariant())
            {
                case "langversion":
                    LangVersion = Text(value);
                    break;
                default:
                    WarnUnknown(path, "check", key);
                    break;
            }
        }
    }

    private void ReadBuild(YamlMappingNode section, string path)
    {
        foreach (var (key, value) in Members(section))
        {
            switch (key.ToLowerInvariant())
            {
                case "assemblyname":
                    AssemblyName = Text(value);
                    break;
                case "configuration":
                    Configuration = Text(value);
                    break;
                case "output":
                    Output = Text(value);
                    break;
                case "outputtype":
                    OutputType = Choice(value, NcBuild.OutputTypes, NcBuild.DefaultOutputType, key, path);
                    break;
                case "extraargs":
                    ExtraArgs = Text(value);
                    break;
                case "defineconstants":
                    DefineConstants = Text(value);
                    break;
                case "rootnamespace":
                    RootNamespace = Text(value);
                    break;
                case "platformtarget":
                    PlatformTarget = Choice(value, NcBuild.PlatformTargets, NcBuild.DefaultPlatformTarget, key, path);
                    break;
                case "pathmap":
                    PathMap = Text(value);
                    break;
                case "features":
                    Features = Text(value);
                    break;
                case "analysislevel":
                    AnalysisLevel = Text(value);
                    break;
                case "debugtype":
                    DebugType = Choice(value, NcBuild.DebugTypes, NcBuild.DefaultDebugType, key, path);
                    break;
                case "documentationfile":
                    DocumentationFile = Text(value);
                    break;
                case "compilergeneratedfilesoutputpath":
                    CompilerGeneratedFilesOutputPath = Text(value);
                    break;
                case "warninglevel":
                    WarningLevel = Number(value, key, path);
                    break;
                case "nowarn":
                    NoWarn = Ids(value);
                    break;
                case "warningsaserrors":
                    WarningsAsErrors = Ids(value);
                    break;
                case "warningsnotaserrors":
                    WarningsNotAsErrors = Ids(value);
                    break;
                case "nullable":
                    Nullable = Flag(value, key, path);
                    break;
                case "implicitusings":
                    ImplicitUsings = Flag(value, key, path);
                    break;
                case "dependsonmodapi":
                    DependsOnModApi = Flag(value, key, path);
                    break;
                case "allowunsafeblocks":
                    AllowUnsafeBlocks = Flag(value, key, path);
                    break;
                case "shareddependencies":
                    SharedDependencies = Flag(value, key, path);
                    break;
                case "optimize":
                    Optimize = Flag(value, key, path);
                    break;
                case "checkforoverflowunderflow":
                    CheckForOverflowUnderflow = Flag(value, key, path);
                    break;
                case "treatwarningsaserrors":
                    TreatWarningsAsErrors = Flag(value, key, path);
                    break;
                case "deterministic":
                    Deterministic = Flag(value, key, path);
                    break;
                case "enablenetanalyzers":
                    EnableNETAnalyzers = Flag(value, key, path);
                    break;
                case "enforcecodestyleinbuild":
                    EnforceCodeStyleInBuild = Flag(value, key, path);
                    break;
                case "debugsymbols":
                    DebugSymbols = Flag(value, key, path);
                    break;
                case "generatedocumentationfile":
                    GenerateDocumentationFile = Flag(value, key, path);
                    break;
                case "emitcompilergeneratedfiles":
                    EmitCompilerGeneratedFiles = Flag(value, key, path);
                    break;
                default:
                    WarnUnknown(path, "build", key);
                    break;
            }
        }
    }

    //Members the scalar keys of a mapping, quoted or not
    private static IEnumerable<(string Key, YamlNode Value)> Members(YamlMappingNode node)
    {
        foreach (var pair in node.Children)
        {
            if (pair.Key is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value))
                yield return (scalar.Value!.Trim(), pair.Value);
        }
    }

    private static string? Text(YamlNode node) => (node as YamlScalarNode)?.Value?.Trim();

    //Ids accepts a semicolon separated scalar or a yaml sequence, since both read naturally for a warning id list
    private static IReadOnlyList<string>? Ids(YamlNode node)
    {
        if (node is YamlScalarNode scalar)
            return Split(scalar.Value ?? string.Empty).ToList();

        if (node is not YamlSequenceNode sequence)
            return null;

        var ids = new List<string>();
        foreach (var item in sequence.Children)
        {
            if (item is YamlScalarNode entry && !string.IsNullOrWhiteSpace(entry.Value))
                ids.Add(entry.Value.Trim());
        }

        return ids;
    }

    private static bool? Flag(YamlNode node, string key, string path)
    {
        if (bool.TryParse(Text(node), out var value))
            return value;

        Warn($"{path}: {key} must be true or false");
        return null;
    }

    private static int? Number(YamlNode node, string key, string path)
    {
        if (int.TryParse(Text(node), out var value))
            return value;

        Warn($"{path}: {key} must be a number");
        return null;
    }

    private static string? Choice(YamlNode node, IReadOnlyList<string> known, string fallback, string key, string path)
    {
        var value = Text(node);
        if (string.IsNullOrEmpty(value))
            return null;

        var match = known.FirstOrDefault(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;

        Warn($"{path}: unknown {key} \"{value}\", known ones are {string.Join(", ", known)}, using {fallback}");
        return fallback;
    }

    private static void WarnUnknown(string path, string section, string key)
        => Warn($"{path}: unknown {key} under {section}, ignored");

    private static void Warn(string message)
        => NacreCraft.Pit.Tools.Progress.WriteLine($"warning: {message}");
}

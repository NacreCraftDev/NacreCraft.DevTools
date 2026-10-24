using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Compile;

public sealed record CompileOptions(string AssemblyName, string LangVersion, bool Nullable, bool ImplicitUsings,
    string DefineConstants, OutputKind OutputKind, bool AllowUnsafe, bool Lint)
{
    //Matches the template project's defaults and is used when only checking
    public static CompileOptions Default { get; } =
        new("ncm-project", NcCheck.DefaultLangVersion, true, true, string.Empty,
            OutputKind.DynamicallyLinkedLibrary, NcBuild.DefaultAllowUnsafe, false);

    //Builds options from the project config, with fallbackName covering a missing assembly name
    public static CompileOptions From(NcProject project, string? fallbackName = null, bool lint = false)
        => new(
            string.IsNullOrWhiteSpace(project.Build.AssemblyName)
                ? fallbackName ?? "ncm-project"
                : project.Build.AssemblyName,
            project.Check.LangVersion,
            project.Build.Nullable,
            project.Build.ImplicitUsings,
            project.Build.DefineConstants,
            string.Equals(project.Build.OutputType, NcBuild.ExeOutputType, StringComparison.OrdinalIgnoreCase)
                ? OutputKind.ConsoleApplication
                : OutputKind.DynamicallyLinkedLibrary,
            project.Build.AllowUnsafe,
            lint);
}

public sealed class ProjectCompilation
{
    //Path of the virtual tree carrying implicit usings, whose diagnostics are never reported
    public const string GeneratedPath = "ncm.g.cs";

    internal ProjectCompilation(Compilation compilation, CSharpParseOptions parseOptions,
        IReadOnlyList<string> sources, IReadOnlyList<string> references)
    {
        Compilation = compilation;
        ParseOptions = parseOptions;
        Sources = sources;
        References = references;
    }

    //Result after the source generators have run
    public Compilation Compilation { get; }

    //Shared with the generators so both parse the same way
    public CSharpParseOptions ParseOptions { get; }

    //Project sources, excluding the virtual tree
    public IReadOnlyList<string> Sources { get; }

    //Reference files used by this compilation, also watched by the incremental check
    public IReadOnlyList<string> References { get; }
}

//Assembles sources, references, implicit usings and source generators into one compilation shared by check and build
public static class CompilationFactory
{
    //Global usings injected when implicit usings are enabled, without which every source file fails to find System types
    private const string ImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    //Returns null on failure and writes the reason into error
    //progress receives the finished count, the total and the file, which a tool can show while a large project parses
    public static ProjectCompilation? Create(string root, CompileOptions options, out string error,
        Action<int, int, string>? progress = null)
    {
        error = string.Empty;

        //an aggregator owns no sources, so there is nothing to compile and no diagnostics to report
        if (NcProject.TryFind(root, out _) is { Bootstrap: true })
        {
            error = "a bootstrap config has no sources of its own";
            return null;
        }

        var files = SourceFiles.Enumerate(root).ToList();
        if (files.Count == 0)
        {
            error = "no C# source file found in this project";
            return null;
        }

        var parseOptions = ParseOptionsOf(options);
        var parsed = new SyntaxTree?[files.Count];

        //each file reads and parses on its own, so a large project uses every core instead of walking the list once
        var done = 0;
        Parallel.For(0, files.Count, index =>
        {
            parsed[index] = ParseFile(files[index], parseOptions);
            if (progress is not null)
                progress(Interlocked.Increment(ref done), files.Count, files[index]);
        });

        var trees = new List<SyntaxTree>(files.Count + 1);

        if (options.ImplicitUsings)
        {
            trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsings, parseOptions,
                path: ProjectCompilation.GeneratedPath, encoding: Encoding.UTF8));
        }

        foreach (var tree in parsed)
        {
            if (tree is not null)
                trees.Add(tree);
        }

        var referencePaths = ReferencePaths(root);
        var compilation = CSharpCompilation.Create(
            options.AssemblyName,
            trees,
            LoadReferences(referencePaths),
            new CSharpCompilationOptions(
                options.OutputKind,
                allowUnsafe: options.AllowUnsafe,
                nullableContextOptions: options.Nullable
                    ? NullableContextOptions.Enable
                    : NullableContextOptions.Disable,
                optimizationLevel: OptimizationLevel.Release,
                deterministic: true,
                //injection targets and their members are often internal or private, and the default import hides them,
                //which would make the hook check report a kernel method as missing; accessibility is still enforced when binding
                metadataImportOptions: MetadataImportOptions.All));

        //UI projects have members produced by generators, so they must run first or the sources fail to resolve those names
        var generated = RunGenerators(root, compilation, parseOptions);
        return new ProjectCompilation(generated, parseOptions, files, referencePaths);
    }

    //Every file that can affect the output; missing one risks building a stale product
    public static IReadOnlyList<string> Inputs(string root)
    {
        var paths = new List<string>(SourceFiles.Enumerate(root));
        paths.AddRange(ReferencePaths(root));
        return paths;
    }

    private static CSharpParseOptions ParseOptionsOf(CompileOptions options)
    {
        var parse = new CSharpParseOptions(LanguageVersionOf(options.LangVersion));
        var symbols = Symbols(options.DefineConstants);
        return symbols.Count == 0 ? parse : parse.WithPreprocessorSymbols(symbols);
    }

    //Falls back to the latest version when the value is not recognized
    private static LanguageVersion LanguageVersionOf(string value)
    {
        if (string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase))
            return LanguageVersion.Latest;

        if (string.Equals(value, "preview", StringComparison.OrdinalIgnoreCase))
            return LanguageVersion.Preview;

        //Dotted versions like 12.0 map to CSharp12
        var major = value.Split('.')[0];
        return Enum.TryParse<LanguageVersion>("CSharp" + major, ignoreCase: true, out var version)
            ? version
            : LanguageVersion.Latest;
    }

    //Splits define constants on ; or , keeping only the part before = as in TRACE=1
    private static List<string> Symbols(string value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(symbol => symbol.Split('=')[0].Trim())
                .Where(symbol => symbol.Length > 0)
                .ToList();

    //Trees already parsed, keyed by path; a check only reparses the files that changed since the last one
    private static readonly ConcurrentDictionary<string, ParsedFile> ParsedFiles =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record ParsedFile(long Length, DateTime WrittenUtc, string Stamp, SyntaxTree Tree);

    //ParseFile reads and parses one source, answering the tree kept from an earlier run when nothing changed
    private static SyntaxTree? ParseFile(string path, CSharpParseOptions parseOptions)
    {
        var stamp = Stamp(parseOptions);
        try
        {
            var info = new FileInfo(path);
            if (ParsedFiles.TryGetValue(path, out var cached)
                && cached.Length == info.Length
                && cached.WrittenUtc == info.LastWriteTimeUtc
                && cached.Stamp == stamp)
            {
                return cached.Tree;
            }

            var text = SourceTextCache.Read(path);
            if (text is null)
            {
                Trace.Log($"cannot read {path}");
                return null;
            }

            //The encoding has to reach Roslyn or debug info cannot be generated
            var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path: path, encoding: Encoding.UTF8);
            ParsedFiles[path] = new ParsedFile(info.Length, info.LastWriteTimeUtc, stamp, tree);
            return tree;
        }
        catch (IOException e)
        {
            Trace.Log($"cannot read {path}: {e.Message}");
            return null;
        }
    }

    //Stamp identifies the parse settings a cached tree was made with, since a different language version parses differently
    private static string Stamp(CSharpParseOptions options)
        => $"{options.LanguageVersion}|{string.Join(";", options.PreprocessorSymbolNames)}";

    //Reference files for this compilation, also needed by the targets side
    internal static IReadOnlyList<string> References(string root) => ReferencePaths(root);

    //Reference assemblies come first because building against implementation assemblies would bind the output to concrete implementations
    private static List<string> ReferencePaths(string root)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in RefPack.Assemblies())
            paths.Add(path);

        if (paths.Count == 0 && AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
        }

        //NC assemblies under the project's Build/kernel, synced from the template; only this level is scanned since restored packages are covered elsewhere
        var kernel = Path.Combine(root, ProjectLayout.Kernel);
        if (Directory.Exists(kernel))
        {
            foreach (var path in Directory.EnumerateFiles(kernel, "*.dll", SearchOption.AllDirectories))
                paths.Add(path);
        }

        //Assemblies of restored packages scanned straight from their target, one directory per package
        foreach (var path in PackageResolver.Assemblies(root))
            paths.Add(path);

        //The two reference kinds declared in the config: raw dlls and other projects' output
        var config = NcProject.TryFind(root, out _);
        if (config is not null)
        {
            foreach (var pattern in config.FileReferences)
            {
                foreach (var file in PathPattern.Match(root, pattern))
                {
                    if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        paths.Add(file);
                }
            }

            foreach (var product in ProjectReferences.Products(root, config))
                paths.Add(product);

            //A project whose output name matches a kernel assembly never compiles against that copy
            //Building the mod api means Build/kernel holds the NacreCraft.ModApi.dll shipped with the server, and a reference
            //to that older assembly makes the XAML compiler resolve it by name and report the same XAML loaded twice
            var self = config.AssemblyNameOf(root);
            paths.RemoveWhere(path => string.Equals(
                Path.GetFileNameWithoutExtension(path), self, StringComparison.OrdinalIgnoreCase));
        }
        //Without an ncproj the project is treated as a plain csproj whose references msbuild resolves, which is also the path --no-manifest check takes
        else
        {
            foreach (var path in CsprojReferences.Assemblies(root))
                paths.Add(path);
        }

        return paths.ToList();
    }

    //Metadata references already opened, keyed by path; the same kernel and packages are loaded on every check
    private static readonly ConcurrentDictionary<string, MetadataReference> LoadedReferences =
        new(StringComparer.OrdinalIgnoreCase);

    //Loads reference files into Roslyn metadata references, skipping any that fail to load
    private static List<MetadataReference> LoadReferences(IReadOnlyList<string> paths)
    {
        var loaded = new MetadataReference?[paths.Count];

        //reference files are independent, so opening them in parallel keeps a cold check from waiting on one disk at a time
        Parallel.For(0, paths.Count, index =>
        {
            var path = paths[index];
            if (LoadedReferences.TryGetValue(path, out var cached))
            {
                loaded[index] = cached;
                return;
            }

            try
            {
                var reference = MetadataReference.CreateFromFile(path);
                LoadedReferences[path] = reference;
                loaded[index] = reference;
            }
            catch (Exception e)
            {
                Trace.Log($"failed to load reference {path}: {e.GetType().Name}");
            }
        });

        var references = loaded.Where(reference => reference is not null).Cast<MetadataReference>().ToList();
        Trace.Log($"Roslyn loaded {references.Count} reference(s)");
        return references;
    }

    //Runs the packages' generators and merges their output; the UI InitializeComponent and x:Name members exist only after this step
    private static Compilation RunGenerators(string root, Compilation compilation, CSharpParseOptions parseOptions)
    {
        var analyzers = PackageResolver.Analyzers(root).ToList();
        if (analyzers.Count == 0)
            return compilation;

        var generators = new List<ISourceGenerator>();
        foreach (var path in analyzers)
            LoadGenerators(path, generators);

        if (generators.Count == 0)
        {
            Trace.Log($"loaded {analyzers.Count} analyzer assembly(ies), none carries a source generator");
            return compilation;
        }

        //Template files reach the generators as additional files, the only path by which axaml gets read
        var additional = SourceFiles.Enumerate(root, "*.axaml")
            .Select(path => (AdditionalText)new AdditionalFile(path))
            .ToList();

        var driver = CSharpGeneratorDriver.Create(generators, additional, parseOptions, new OptionsProvider());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var failures);
        Trace.Log($"ran {generators.Count} generator(s) over {additional.Count} additional file(s), {failures.Length} diagnostic(s)");
        foreach (var failure in failures)
            Trace.Log($"generator diagnostic {failure.Id}: {failure.GetMessage()}");

        foreach (var tree in output.SyntaxTrees.Except(compilation.SyntaxTrees))
            Trace.Log($"generated tree {tree.FilePath}");
        return output;
    }

    //Picks generators out of an analyzer assembly; only parameterless constructors qualify since that is how Roslyn instantiates them
    private static void LoadGenerators(string path, List<ISourceGenerator> generators)
    {
        System.Reflection.Assembly assembly;
        try
        {
            assembly = System.Reflection.Assembly.LoadFrom(path);
        }
        catch (Exception e)
        {
            Trace.Log($"cannot load analyzer {path}: {e.GetType().Name}");
            return;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException e)
        {
            //A missing dependency should not void the whole assembly, so keep whatever types did load
            types = e.Types.Where(type => type is not null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || type.GetConstructor(Type.EmptyTypes) is null)
                continue;

            try
            {
                if (typeof(IIncrementalGenerator).IsAssignableFrom(type))
                    generators.Add(((IIncrementalGenerator)Activator.CreateInstance(type)!).AsSourceGenerator());
                else if (typeof(ISourceGenerator).IsAssignableFrom(type))
                    generators.Add((ISourceGenerator)Activator.CreateInstance(type)!);
                else
                    continue;

                Trace.Log($"generator {type.FullName} from {Path.GetFileName(path)}");
            }
            catch (Exception e)
            {
                Trace.Log($"cannot instantiate generator {type.FullName}: {e.GetType().Name}");
            }
        }
    }

    private sealed class AdditionalFile(string path) : AdditionalText
    {
        public override string Path { get; } = path;

        public override SourceText? GetText(CancellationToken cancellationToken = default)
        {
            var text = SourceTextCache.Read(Path);
            if (text is null)
            {
                Trace.Log($"cannot read {Path}");
                return null;
            }

            return SourceText.From(text);
        }
    }

    //Additional files must report the AvaloniaXaml item group, which the UI generators filter on and otherwise produce nothing
    private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
    {
        private static readonly AnalyzerConfigOptions None = new EmptyOptions();

        private static readonly AnalyzerConfigOptions Xaml = new XamlOptions();

        public override AnalyzerConfigOptions GlobalOptions => None;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => None;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Xaml;
    }

    //Missing values must be null because an empty string makes generators treat the property as present
    private sealed class EmptyOptions : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            Trace.Log($"global option {key}");
            value = null!;
            return false;
        }
    }

    //Only answers the source item group query
    private sealed class XamlOptions : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            Trace.Log($"additional option {key}");
            if (key == "build_metadata.AdditionalFiles.SourceItemGroup")
            {
                value = "AvaloniaXaml";
                return true;
            }

            value = null!;
            return false;
        }
    }
}

using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using NacreCraft.ModBuild.Tools;

namespace NacreCraft.NcMod.LangServer;

//NcModChecker runs the passes `ncm hook` runs against the project holding one document
//A manifest is checked for its rules, a C# file for the whole project, since an error anywhere in it is worth showing
internal sealed class NcModChecker(Action<int, string> log)
{
    //Kernel, packages and project references of a project, none of which a source save changes
    private readonly Dictionary<string, Prepared?> _prepared = new(StringComparer.OrdinalIgnoreCase);

    //Check inspects the project the document belongs to, returning everything the passes found
    public IReadOnlyList<Diagnostic> Check(string path, out string? root)
    {
        root = Locate(path);
        if (root is null)
            return [];

        var prepared = Prepare(root);
        if (prepared is null)
            return [];

        //a bootstrap config only aggregates other projects and has no sources or manifest of its own
        if (prepared.Config.Bootstrap)
            return [];

        var bag = new DiagnosticBag();

        //the same order the command line check follows, so both say the same thing about the same sources
        TemplateStore.Configure(prepared.Config);
        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            log(3, "the template catalog is unavailable, the mod api check is skipped");
        var covered = catalog is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : ModApiAnalyzer.Analyze(root, catalog, bag);

        var project = CSharpAnalyzer.Analyze(root, prepared.Options, bag, covered);

        //the sources have to compile before the rules that read them can be checked
        if (bag.HasErrors)
            return bag.Sorted().ToList();

        var manifest = ModProject.TryFind(root);
        if (project is null)
        {
            log(1, "the project produced no compilation, the injection rules were not checked");
            return bag.Sorted().ToList();
        }

        if (manifest is null)
            return bag.Sorted().ToList();

        var rules = new List<HookRule>();
        var mixins = new List<MixinRule>();
        HookChecker.ReadManifest(manifest.Document, manifest.ManifestPath, root, rules, mixins, bag);
        HookChecker.ReadAnnotations(project.Compilation, root, rules, mixins);
        HookChecker.Check(project.Compilation, rules, mixins, bag);

        log(3, $"checked {rules.Count + mixins.Count} rule(s), {bag.ErrorCount} error(s), {bag.WarningCount} warning(s)");

        return bag.Sorted().ToList();
    }

    //Locate finds the project directory a document belongs to, walking up from the file like the command line does
    private static string? Locate(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            return null;

        var config = NcProject.TryFind(directory, out _);
        return config is null ? null : Path.GetDirectoryName(config.Path);
    }

    //Prepare resolves the kernel, the packages and the references once per project
    private Prepared? Prepare(string root)
    {
        if (_prepared.TryGetValue(root, out var cached))
            return cached;

        Prepared? prepared = null;
        try
        {
            var config = NcProject.TryFind(root, out var configError);
            log(3, $"preparing {root}: config={config is not null} error={configError}");
            if (config is not null && config.Bootstrap)
            {
                //nothing to restore or reference for an aggregator, and the projects it names load on their own
                prepared = new Prepared(config, CompileOptions.From(config));
            }
            else if (config is not null)
            {
                var kernel = KernelStore.Sync(root, config);
                var restore = RestoreTool.Ensure(config, RestoreTool.DirectoryOf(config), out var restoreError);
                ProjectReferences.Clear();
                var references = ProjectReferences.Build(root, config, out var referenceError);
                log(3, $"kernel={kernel} restore={restore}({restoreError}) references={references}({referenceError})");

                prepared = new Prepared(config, CompileOptions.From(config));
            }
        }
        catch (Exception e)
        {
            //a failed preparation must not take the editor session down, the next save tries again
            log(1, $"cannot prepare {root}: {e.Message}");
            prepared = null;
        }

        _prepared[root] = prepared;
        return prepared;
    }

    //Prepared what a check needs beyond the sources, which are read again on every run
    private sealed record Prepared(NcProject Config, CompileOptions Options);
}

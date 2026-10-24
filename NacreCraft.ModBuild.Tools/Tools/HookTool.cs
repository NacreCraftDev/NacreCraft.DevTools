using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Tools;

//HookTool checks the injection rules of a mod before the server ever loads it
//A rule naming a type that is not there, an injection form that is misspelled or a replacement whose signature cannot carry
//the call it replaces otherwise only shows up at startup, where finding out costs a reload; everything here is decided from
//the sources and the references at hand. The rules are the ones in NacreCraft/docs/injection.md
internal static class HookTool
{
    //ProjectOption points the check at another directory
    private const string ProjectOption = "--project";

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("hook", "Check the injection rules of a mod in ncmod.json and in Annotations against the types they name", Run,
        [
            new($"{ProjectOption} <dir>", "Check the project in that directory instead of the current one"),
        ]);

    //Run checks the project in place: syntax first, then every rule the manifest and the annotations declare
    private static int Run(string[] args)
    {
        string root;
        try
        {
            root = Root(args);
        }
        catch (FormatException e)
        {
            Console.WriteLine($"error: {e.Message}");
            Console.WriteLine($"Usage: ncm hook [{ProjectOption} <dir>]");
            return 1;
        }

        var config = NcProject.TryFind(root, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        if (config is null)
        {
            Console.WriteLine("error: no ncproj found here, run this inside a project or point --project at one");
            return 1;
        }

        var manifest = ModProject.TryFind(root);
        if (manifest is null)
        {
            //a manifest that is there but unreadable has already been reported with its line, so it is not missing
            Console.WriteLine(File.Exists(Path.Combine(root, ModProject.ManifestName))
                ? "error: the manifest could not be read, see the error above"
                : $"error: no {ModProject.ManifestName} here, only a mod project declares injection rules");
            return 1;
        }

        var options = CompileOptions.From(config);

        //the same preparation the build does, since the check compiles against every reference the build would use
        if (!KernelStore.Sync(root, config))
        {
            Console.WriteLine("error: the kernel references could not be prepared");
            return 1;
        }

        if (!RestoreTool.Ensure(config, RestoreTool.DirectoryOf(config), out var restoreError))
        {
            Console.WriteLine($"error: {restoreError}");
            return 1;
        }

        //project references are resolved once here, the compilation then reads their outputs from the cache
        ProjectReferences.Clear();
        if (!ProjectReferences.Build(root, config, out var referenceError))
        {
            Console.WriteLine($"error: {referenceError}");
            return 1;
        }

        //an annotation is a type the mod api provides, so a project that turned the api off cannot carry one: it would not
        //even compile, and the missing type would be the only thing the author sees. Read from the syntax, which needs no
        //compilation for the same reason
        if (!config.Build.DependsOnModApi)
        {
            var written = HookChecker.WrittenAnnotations(root);
            if (written > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"error: this project turns the mod api off, but {written} Inject or Mixin annotation(s) "
                    + "are written in its sources, and those come from the mod api");
                Console.ResetColor();
                Console.WriteLine("       write them as `hooks` or `mixins` entries in ncmod.json instead, or drop the switch");
                return 1;
            }
        }

        //the same pass the build runs, so a rule is never read out of sources that do not compile
        var bag = new DiagnosticBag();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        TemplateStore.Configure(config);
        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            Console.WriteLine("warning: template catalog is unavailable, the mod api check is skipped");
        else
            covered = ModApiAnalyzer.Analyze(root, catalog, bag);

        var project = CSharpAnalyzer.Analyze(root, options, bag, covered);

        if (bag.HasErrors)
        {
            DiagnosticRenderer.Render(bag.Sorted().ToList());
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("error: the sources do not pass the syntax check, the injection rules were not examined");
            Console.ResetColor();
            return 1;
        }

        if (project is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("error: the project has no compilable source, the injection rules were not examined");
            Console.ResetColor();
            return 1;
        }

        var rules = new List<HookRule>();
        var mixins = new List<MixinRule>();
        HookChecker.ReadManifest(manifest.Document, manifest.ManifestPath, root, rules, mixins, bag);
        HookChecker.ReadAnnotations(project.Compilation, root, rules, mixins);

        var dynamic = HookChecker.Check(project.Compilation, rules, mixins, bag);

        var diagnostics = bag.Sorted().ToList();
        if (diagnostics.Count > 0)
            DiagnosticRenderer.Render(diagnostics);

        var declared = rules.Count + mixins.Count;
        Console.WriteLine($"Checked {declared} rule(s): {bag.ErrorCount} error(s), {bag.WarningCount} warning(s)");
        if (dynamic > 0)
            Console.WriteLine($"{dynamic} dynamic rule(s) were left alone, runtime injection is checked elsewhere");

        if (!bag.HasErrors)
            return 0;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {bag.ErrorCount} injection rule(s) cannot land as written");
        Console.ResetColor();
        return 1;
    }

    //Root reads the optional project directory, defaulting to where the command was issued
    private static string Root(string[] args)
    {
        var root = Environment.CurrentDirectory;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] != ProjectOption)
                throw new FormatException($"unknown hook argument {args[index]}");

            if (index + 1 >= args.Length)
                throw new FormatException($"{ProjectOption} needs a directory after it");

            var given = args[++index];
            if (!Directory.Exists(given))
                throw new FormatException($"no directory at {given}");

            root = Path.GetFullPath(given);
        }

        return root;
    }
}

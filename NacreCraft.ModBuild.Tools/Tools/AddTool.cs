using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;

namespace NacreCraft.ModBuild.Tools;

//AddTool adds a dependency, a reference or an injection rule to the project
//nuget already works, mod waits for the cloud mod server
internal static class AddTool
{
    private const string NugetKind = "nuget";
    private const string ModKind = "mod";

    //FileKind lands in the <File> element of <References>
    private const string FileKind = "file";

    //ProjectKind lands in the <Project> element of <References>
    private const string ProjectKind = "project";

    //HookKind appends to the hooks array of ncmod.json, which is the manifest route of an injection rule
    private const string HookKind = "hook";

    //Options a hook rule takes, beyond its four names
    //patch-mode maps to the patchMode field, the rest share their name with the field
    private static readonly string[] HookOptions = ["type", "patch-mode", "placement", "environment", "label"];

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("add", "Add a dependency, a reference or an injection rule to the project", Run,
        [
            new($"{NugetKind} <id> [version]", "Add a nuget package, the version takes the same syntax as dotnet add package"),
            new($"{FileKind} <path>", "Add a managed dll as a compile reference, relative to the project root"),
            new($"{ProjectKind} <path>", "Add another project as a compile reference, built first and referenced by its output"),
            new($"{ModKind} <id> [version]", "Add a mod dependency, not implemented yet"),
            new($"{HookKind} <target> <method> <replaceType> <replaceMethod> [options]",
                "Append a rule to the hooks array of ncmod.json, replacing a call site with one of this mod's methods"),
            new($"  --type <form>", $"Injection form, one of {string.Join(", ", HookChecker.HookTypes)}, CallSite by default"),
            new($"  --patch-mode <mode>", $"Landing route, one of {string.Join(", ", HookChecker.PatchModes)}, ILRewrite by default"),
            new($"  --placement <place>", $"How the anchor lands, one of {string.Join(", ", HookChecker.Placements)}, Replace by default"),
            new($"  --environment <side>", $"Side the rule applies to, one of {string.Join(", ", HookChecker.Environments)}, both by default"),
            new($"  --label <text>", "Probe label, used by Mark and Probe"),
        ]);

    //Run validates the arguments for the chosen kind and hands them to the config
    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        //A rule takes its own shape, four names plus options, so it is handled before the kinds that take one value
        if (string.Equals(args[0], HookKind, StringComparison.OrdinalIgnoreCase))
            return AddHook(args[1..]);

        if (args.Length < 2)
        {
            Usage();
            return 1;
        }

        var kind = args[0];
        var value = args[1];

        if (string.Equals(kind, ModKind, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: {ModKind} dependencies are not implemented yet, they will come with the cloud mod server");
            return 1;
        }

        var isNuget = string.Equals(kind, NugetKind, StringComparison.OrdinalIgnoreCase);
        var isFile = string.Equals(kind, FileKind, StringComparison.OrdinalIgnoreCase);
        var isProject = string.Equals(kind, ProjectKind, StringComparison.OrdinalIgnoreCase);
        if (!isNuget && !isFile && !isProject)
        {
            Console.WriteLine($"error: unknown kind {kind}");
            Usage();
            return 1;
        }

        //Only nuget takes an optional version, the other kinds reject an extra argument
        var limit = isNuget ? 3 : 2;
        if (args.Length > limit)
        {
            Usage();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            Console.WriteLine(isNuget ? "error: a package id is required" : "error: a path is required");
            return 1;
        }

        var version = args.Length == 3 ? args[2] : string.Empty;

        //Validate the version here because a bad one would only surface at restore
        if (isNuget && version.Length > 0 && !VersionRange.TryParse(version, out _))
        {
            Console.WriteLine($"error: {version} is not a valid version or range");
            return 1;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        if (project is null)
        {
            Console.WriteLine($"error: no {NcProject.Extension} in this directory or any parent");
            return 1;
        }

        var added = isNuget
            ? project.AddPackage(value, version, out var error)
            : project.AddReference(value, isProject, out error);

        if (!added)
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        if (!isNuget)
        {
            Console.WriteLine($"Added {(isProject ? "project" : "file")} {value} to {project.Path}");
            return 0;
        }

        Console.WriteLine(version.Length > 0
            ? $"Added {value} {version} to {project.Path}"
            : $"Added {value} to {project.Path}, the version is left open");
        Console.WriteLine("Run 'ncm restore' to fetch it");
        return 0;
    }

    //Usage prints the accepted forms when the arguments are wrong
    private static void Usage()
    {
        Console.WriteLine($"Usage: ncm add {NugetKind} <id> [version]");
        Console.WriteLine($"       ncm add {FileKind} <path>");
        Console.WriteLine($"       ncm add {ProjectKind} <path>");
        Console.WriteLine($"       ncm add {ModKind} <id> [version]");
        HookUsage();
    }

    //AddHook appends one rule to the hooks array of the manifest
    //The entry carries only what was chosen, leaving the fields the loader already defaults to out of the file
    private static int AddHook(string[] args)
    {
        var names = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                names.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (!HookOptions.Contains(name, StringComparer.Ordinal))
            {
                var near = Similarity.Closest(name, HookOptions);
                Console.WriteLine($"error: unknown hook option {arg}"
                    + (near is null ? string.Empty : $", did you mean --{near}?"));
                HookUsage();
                return 1;
            }

            if (index + 1 >= args.Length)
            {
                Console.WriteLine($"error: {arg} needs a value");
                return 1;
            }

            options[name] = args[++index];
        }

        if (names.Count != 4)
        {
            Console.WriteLine("error: a rule needs the target type, the target method, the replacement class and the replacement method");
            HookUsage();
            return 1;
        }

        //The vocabulary is checked here, since a misspelled form would otherwise only come back from `ncm hook` after the file was written
        var type = Check(options, "type", "hook type", HookChecker.HookTypes, "CallSite");
        var mode = Check(options, "patch-mode", "patch mode", HookChecker.PatchModes, "ILRewrite");
        var placement = Check(options, "placement", "placement", HookChecker.Placements, "Replace");
        var environment = Check(options, "environment", "environment", HookChecker.Environments, "both");
        if (type is null || mode is null || placement is null || environment is null)
            return 1;

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        if (project is null)
        {
            Console.WriteLine($"error: no {NcProject.Extension} in this directory or any parent");
            return 1;
        }

        var manifest = ModProject.TryFind(Environment.CurrentDirectory);
        if (manifest is null)
        {
            Console.WriteLine(File.Exists(Path.Combine(Environment.CurrentDirectory, ModProject.ManifestName))
                ? "error: the manifest could not be read, see the error above"
                : $"error: no {ModProject.ManifestName} here, and the hooks array lives in the mod manifest");
            return 1;
        }

        var entry = new JObject
        {
            ["target"] = names[0],
            ["method"] = names[1],
            ["type"] = type,
            ["replaceType"] = names[2],
            ["replaceMethod"] = names[3],
        };

        if (mode != "ILRewrite")
            entry["patchMode"] = mode;
        if (placement != "Replace")
            entry["placement"] = placement;
        if (environment != "both")
            entry["environment"] = environment;
        if (options.TryGetValue("label", out var label) && label.Length > 0)
            entry["label"] = label;

        var conflict = string.Empty;
        var added = manifest.Edit(document =>
        {
            if (document["hooks"] is not JArray hooks)
            {
                hooks = new JArray();
                document["hooks"] = hooks;
            }

            //the loader keys a rule by target, method, form and route, so a second one written that way would never land
            for (var index = 0; index < hooks.Count; index++)
            {
                if (hooks[index] is not JObject existing)
                    continue;

                if (Field(existing, "target") == names[0] && Field(existing, "method") == names[1]
                    && Field(existing, "type", "CallSite") == type
                    && Field(existing, "patchMode", "ILRewrite") == mode)
                {
                    conflict = $"hooks[{index}] already declares {names[0]}::{names[1]} [{type}/{mode}]";
                    return false;
                }
            }

            hooks.Add(entry);
            return true;
        });

        if (!added)
        {
            //a rejected change is either a conflict or a manifest that cannot be read, both of which are reported
            if (conflict.Length > 0)
                Console.WriteLine($"error: {conflict}");
            return 1;
        }

        Console.WriteLine($"Added {names[0]}::{names[1]} to the hooks of {Path.GetFileName(manifest.ManifestPath)}");
        Console.WriteLine("Run 'ncm hook' to check it against the target");
        return 0;
    }

    //Check reads one option, keeping the default when it is absent and refusing a value the loader does not know
    private static string? Check(Dictionary<string, string> options, string option, string shown, string[] allowed,
        string fallback)
    {
        if (!options.TryGetValue(option, out var value) || value.Length == 0)
            return fallback;

        if (allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            return allowed.First(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));

        var near = Similarity.Closest(value, allowed);
        Console.WriteLine($"error: {value} is not a known {shown}"
            + (near is null ? $", use one of {string.Join(", ", allowed)}" : $", did you mean {near}?"));
        return null;
    }

    //Field reads a string out of a rule already written in the manifest
    private static string Field(JObject entry, string name, string fallback = "")
        => entry.TryGetValue(name, out var value) && value.Type == JTokenType.String
            ? value.Value<string>() ?? fallback
            : fallback;

    //HookUsage prints the shape of a hook rule
    private static void HookUsage()
    {
        Console.WriteLine($"Usage: ncm add {HookKind} <target> <method> <replaceType> <replaceMethod> [options]");
        Console.WriteLine($"       options: {string.Join(", ", HookOptions.Select(option => "--" + option))}");
    }
}

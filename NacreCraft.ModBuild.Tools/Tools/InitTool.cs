using System.IO.Compression;
using System.Text;
using NacreCraft.ModBuild.Core;
using Newtonsoft.Json.Linq;

namespace NacreCraft.ModBuild.Tools;

//InitTool creates a new mod from the project template and generates the icon along the way
//init works in the current directory, new makes a subdirectory named after the mod
internal static class InitTool
{
    //SourceName the placeholder the template uses for the mod name, in file names and in content alike
    private const string SourceName = "NcModTemplate";

    //TemplateResource the embedded template archive
    private const string TemplateResource = "ncm-template.zip";

    //UpgradeOption converts the created csproj into a .ncproj right away, without keeping a backup
    private const string UpgradeOption = "--upgrade";

    //Register both entry points, they differ only in where the project lands
    //The positional arguments follow the form order, and no arguments at all starts the interactive prompt
    public static void Register()
    {
        ToolRegistry.Register("init", "Initialize a NacreCraft mod project in the current directory",
            args => Run(args, inPlace: true),
        [
            new("[name]", "Mod name, defaults to the directory name"),
            new("[id]", "Mod id, derived from the name when omitted"),
            new("[description]", "Mod description"),
            new("[authors]", "Comma separated author names"),
            new("[homepage]", "Homepage url"),
            new("[sources]", "Source repository url"),
            new("[license]", "License name"),
            new(UpgradeOption, "Convert the created csproj to a .ncproj, removing the csproj"),
        ]);

        ToolRegistry.Register("new", "Create a NacreCraft mod project in a new subdirectory",
            args => Run(args, inPlace: false),
        [
            new("<name>", "Mod name, required"),
            new("[id]", "Mod id, derived from the name when omitted"),
            new("[description]", "Mod description"),
            new("[authors]", "Comma separated author names"),
            new("[homepage]", "Homepage url"),
            new("[sources]", "Source repository url"),
            new("[license]", "License name"),
            new(UpgradeOption, "Convert the created csproj to a .ncproj, removing the csproj"),
        ]);
    }

    //Run collects the content and materializes it
    //No arguments starts the form, arguments are taken positionally in the same order without asking
    //Only init can run into an existing project: new always writes into a subdirectory that has to be free
    private static int Run(string[] args, bool inPlace)
    {
        var directory = Environment.CurrentDirectory;
        var upgrade = args.Contains(UpgradeOption, StringComparer.Ordinal);
        var positional = args.Where(arg => arg != UpgradeOption).ToArray();
        var fields = BuildFields(directory, inPlace);

        if (positional.Length > 0)
        {
            if (positional.Length > fields.Count)
            {
                Console.WriteLine($"{CommandOf(inPlace)} takes at most {fields.Count} arguments: {string.Join(' ', fields.Select(field => field.Key))}");
                return 1;
            }

            //This path is for scripts where nobody sees a confirmation, so an existing project fails outright to avoid damage
            if (inPlace && HasExistingProject(directory))
            {
                Console.WriteLine($"This directory already contains a csproj or {ModProject.ManifestName} file");
                return 1;
            }

            for (var index = 0; index < positional.Length; index++)
                fields[index].Value = positional[index];
        }
        else
        {
            if (inPlace && HasExistingProject(directory) && !Prompt.Confirm(
                    $"This directory already contains a csproj or {ModProject.ManifestName} file. Initializing here may affect the existing project. Continue?",
                    defaultYes: false, warn: true))
                return 0;

            if (!Prompt.Form(fields))
            {
                Console.WriteLine("A mod name is required");
                return 1;
            }
        }

        return Submit(directory, fields, inPlace, upgrade);
    }

    //BuildFields the form fields in positional argument order, changing one place keeps both in sync
    private static List<FormField> BuildFields(string directory, bool inPlace) => new()
    {
        //Only the name is required, the rest may stay empty and fall back to the template or defaults
        //init offers the directory name as the answer, since that is what the folder is already called
        //The name is validated on every keystroke, a taken subdirectory is an error and invalid characters a warning
        new("name", "Mod name", required: true)
        {
            Value = inPlace ? Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) : string.Empty,
            Validate = value => ValidateName(directory, value, inPlace),
        },
        new("id", "Mod id"),
        new("description", "Description"),
        new("authors", "Authors"),
        new("homepage", "Homepage"),
        new("sources", "Sources"),
        new("license", "License"),
    };

    private static string CommandOf(bool inPlace) => inPlace ? "init" : "new";

    //Submit the shared tail of both entry points, validating the name and directory before materializing
    private static int Submit(string directory, List<FormField> fields, bool inPlace, bool upgrade)
    {
        string Value(string key) => fields.First(field => field.Key == key).Value.Trim();

        var name = Value("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("A mod name is required");
            return 1;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Console.WriteLine("The mod name contains characters that cannot be used in a directory name");
            return 1;
        }

        //The interactive path is guarded by form highlighting, this argument path has no watcher so the check moves here
        //new writes into a subdirectory, so a taken one is refused; init has no subdirectory to collide with
        if (!inPlace && Directory.Exists(Path.Combine(directory, name)))
        {
            Console.WriteLine($"A directory named {name} already exists");
            return 1;
        }

        var id = Value("id");
        var answers = new Answers(
            name,
            string.IsNullOrWhiteSpace(id) ? DeriveId(name) : id,
            Value("description"),
            Value("authors"),
            Value("homepage"),
            Value("sources"),
            Value("license"));

        return Execute(directory, answers, inPlace, upgrade);
    }

    //Execute creates the project, fills in the manifest fields, deletes the template icon and draws a new one
    //Separated from collection so this chain can run without interaction
    private static int Execute(string directory, Answers answers, bool inPlace, bool upgrade)
    {
        var target = inPlace ? directory : Path.Combine(directory, answers.Name);
        if (!ExtractTemplate(target, answers.Name))
            return 1;

        if (!File.Exists(Path.Combine(target, ModProject.ManifestName)))
        {
            Console.WriteLine($"Project created but {ModProject.ManifestName} was not found in {target}");
            return 1;
        }

        var project = ModProject.TryFind(target);
        if (project is null)
        {
            Console.WriteLine($"Project created but {ModProject.ManifestName} could not be read in {target}");
            return 1;
        }

        ApplyAnswers(project, answers);

        //The manifest just changed so the display name is re-read, the icon text uses it
        var refreshed = ModProject.TryFind(target) ?? project;

        //The template icon is a placeholder, replaced by one generated from the mod name
        if (File.Exists(refreshed.IconPath))
            File.Delete(refreshed.IconPath);
        IconTool.Generate(refreshed);

        Console.WriteLine($"Created {target}");

        //The template still ships a csproj, but ncm only reads .ncproj, so the fresh project is converted before
        //anything opens it. The template file has no value of its own, hence no backup
        if (upgrade)
        {
            var csproj = Path.Combine(target, answers.Name + ".csproj");
            if (!File.Exists(csproj))
            {
                Console.WriteLine($"error: {Path.GetFileName(csproj)} was not found, cannot upgrade");
                return 1;
            }

            return UpgradeTool.Run([csproj, "--no-backup"]);
        }

        return 0;
    }

    //ExtractTemplate unpacks the embedded template and fills in the name and id
    //The archive travels inside the assembly, so init works without dotnet new or a machine wide template install
    private static bool ExtractTemplate(string target, string name)
    {
        using var stream = typeof(InitTool).Assembly.GetManifestResourceStream(TemplateResource);
        if (stream is null)
        {
            Console.WriteLine("error: the embedded project template is missing from this build");
            return false;
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Directory.CreateDirectory(target);

        foreach (var entry in archive.Entries)
        {
            //a directory entry carries no name of its own
            if (entry.Name.Length == 0)
                continue;

            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            //the project file is named after the source name and has to carry the mod name instead
            if (string.Equals(Path.GetFileName(relative), SourceName + ".csproj", StringComparison.Ordinal))
                relative = Path.Combine(Path.GetDirectoryName(relative) ?? string.Empty, name + ".csproj");

            var path = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var source = entry.Open();
            using var destination = File.Create(path);
            source.CopyTo(destination);
        }

        ReplacePlaceholders(target, name);
        return true;
    }

    //ReplacePlaceholders writes the name and the derived id into the unpacked text files
    private static void ReplacePlaceholders(string target, string name)
    {
        string[] extensions = [".cs", ".csproj", ".json", ".md", ".xml", ".props", ".targets"];
        foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(path);
            var replaced = text
                .Replace(SourceName, name, StringComparison.Ordinal)
                .Replace("__MOD_ID__", DeriveId(name), StringComparison.Ordinal)
                .Replace("__MOD_ENV__", "both", StringComparison.Ordinal);
            if (!string.Equals(replaced, text, StringComparison.Ordinal))
                File.WriteAllText(path, replaced);
        }
    }

    //HasExistingProject whether the directory already holds a project file or a mod manifest
    private static bool HasExistingProject(string directory)
        => Directory.EnumerateFiles(directory, "*.csproj").Any()
            || File.Exists(Path.Combine(directory, ModProject.ManifestName));

    //BadNameChars characters not allowed in a directory name, including both quotes since pasted names often carry them
    private static readonly char[] BadNameChars = BuildBadNameChars();

    //BuildBadNameChars the platform's invalid characters plus the two quotes
    private static char[] BuildBadNameChars()
    {
        var chars = new List<char>(Path.GetInvalidFileNameChars()) { '\'', '"' };
        return chars.ToArray();
    }

    //ValidateName live validation of the name, a taken subdirectory is an error, special characters a warning and empty is neutral
    private static FieldState ValidateName(string directory, string value, bool inPlace)
    {
        if (string.IsNullOrWhiteSpace(value))
            return FieldState.Normal;
        if (!inPlace && Directory.Exists(Path.Combine(directory, value)))
            return FieldState.Error;
        return value.IndexOfAny(BadNameChars) >= 0 ? FieldState.Warn : FieldState.Normal;
    }

    //DeriveId derives the id from the name, aligned with the template's lowerCase rule and folding spaces and symbols into hyphens
    private static string DeriveId(string name)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    //ApplyAnswers writes the filled fields into the manifest and leaves empty ones, keeping the template defaults
    private static void ApplyAnswers(ModProject project, Answers answers)
        => project.Edit(manifest =>
        {
            var changed = false;
            //The template already sets name from -n, but older templates lack the field, so set it to make the icon text use the display name
            changed |= SetString(manifest, "name", answers.Name);
            changed |= SetString(manifest, "id", answers.Id);
            changed |= SetString(manifest, "description", answers.Description);
            changed |= SetString(manifest, "license", answers.License);
            changed |= SetAuthors(manifest, answers.Authors);
            changed |= SetContact(manifest, "homepage", answers.Homepage);
            changed |= SetContact(manifest, "sources", answers.Sources);
            return changed;
        });

    //SetString overwrites only when there is a new value
    private static bool SetString(JObject manifest, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        manifest[key] = value;
        return true;
    }

    //SetAuthors splits a comma separated list into an array
    private static bool SetAuthors(JObject manifest, string value)
    {
        var names = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
            return false;

        var array = new JArray();
        foreach (var author in names)
            array.Add(author);
        manifest["authors"] = array;
        return true;
    }

    //SetContact the contact info is a nested object, created when missing
    private static bool SetContact(JObject manifest, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (manifest["contact"] is not JObject contact)
        {
            contact = new JObject();
            manifest["contact"] = contact;
        }

        contact[key] = value;
        return true;
    }

    //Answers what the form collected
    private sealed record Answers(
        string Name,
        string Id,
        string Description,
        string Authors,
        string Homepage,
        string Sources,
        string License);
}

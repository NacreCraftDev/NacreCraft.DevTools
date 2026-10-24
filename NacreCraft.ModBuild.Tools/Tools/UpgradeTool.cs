using System.Xml;
using System.Xml.Linq;
using NacreCraft.ModBuild.Core;
using TargetFramework = NacreCraft.ModBuild.Compile.TargetFramework;

namespace NacreCraft.ModBuild.Tools;

//UpgradeTool converts a csproj based mod project into a .ncproj one
//The original csproj is renamed as a backup and properties ncm cannot map are listed for manual handling
internal static class UpgradeTool
{
    //BackupExtension is the suffix given to the renamed original file
    private const string BackupExtension = ".bak";

    //NoBackupOption drops the original instead of keeping a .bak next to the result
    private const string NoBackupOption = "--no-backup";

    //DependencyTargetName is the template target that embeds dependencies, which ncm takes over so it is not worth reporting
    private const string DependencyTargetName = "EmbedDependencies";

    //DeployTargetName is the template target that copies the output to the host directories
    private const string DeployTargetName = "DeployModToHosts";

    //RuntimeTargetName is the target that drops the msbuild runtime copies, which ncm does not use
    private const string RuntimeTargetName = "DropMsBuildRuntime";

    public static void Register()
        => ToolRegistry.Register("upgrade", "Convert a csproj based mod project to a .ncproj one", Run,
        [
            new("[file]", "The csproj to convert, defaults to the only csproj in the current directory"),
            new(NoBackupOption, "Delete the original csproj instead of keeping a .bak copy"),
        ]);

    //Run finds the source file, reads it, writes the .ncproj and then backs up or drops the original
    //It is shared with init, which converts the project it just created
    internal static int Run(string[] args)
    {
        var noBackup = args.Contains(NoBackupOption, StringComparer.Ordinal);
        var positional = args.Where(arg => arg != NoBackupOption).ToArray();

        if (positional.Length > 1)
        {
            Console.WriteLine($"Usage: ncm upgrade [file] [{NoBackupOption}]");
            return 1;
        }

        var source = positional.Length == 1 ? Path.GetFullPath(positional[0]) : SingleCsproj(Environment.CurrentDirectory);
        if (source is null)
            return 1;

        if (!File.Exists(source))
        {
            Console.WriteLine($"error: {source} does not exist");
            return 1;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(source);
        }
        catch (XmlException e)
        {
            Console.WriteLine($"error: {source} is not valid xml: {e.Message}");
            return 1;
        }

        if (document.Root is not { } root || !string.Equals(root.Name.LocalName, "Project", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: {source} has no <Project> root element");
            return 1;
        }

        //Stop when the same-named target exists, since overwriting a hand-written config is far worse than an error
        var target = Path.ChangeExtension(source, NcProject.Extension);
        if (File.Exists(target))
        {
            Console.WriteLine($"error: {Path.GetFileName(target)} already exists, move it away first");
            return 1;
        }

        var migration = Read(root);
        Console.WriteLine($"Converting {Path.GetFileName(source)}");

        try
        {
            XmlFile.Save(target, Compose(migration, GlobalConfig.Load()));
            if (noBackup)
                File.Delete(source);
            else
                File.Move(source, source + BackupExtension, overwrite: true);
        }
        catch (IOException e)
        {
            Console.WriteLine($"error: {e.Message}");
            return 1;
        }

        Report(migration, source, target, noBackup);
        return 0;
    }

    //SingleCsproj is the only csproj in the directory, erroring when there are several or none
    private static string? SingleCsproj(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.csproj").ToList();
        if (files.Count == 1)
            return files[0];

        Console.WriteLine(files.Count == 0
            ? "error: no .csproj in this directory, pass one explicitly"
            : "error: more than one .csproj in this directory, pass the one to convert");
        return null;
    }

    //Read picks out what maps cleanly and records the rest by category
    private static Migration Read(XElement root)
    {
        var migration = new Migration();

        foreach (var group in root.Elements().Where(element => Folded(element, "PropertyGroup")))
        {
            foreach (var property in group.Elements())
                ReadProperty(property, migration);
        }

        foreach (var group in root.Elements().Where(element => Folded(element, "ItemGroup")))
        {
            foreach (var item in group.Elements())
                ReadItem(item, migration);
        }

        foreach (var target in root.Elements().Where(element => Folded(element, "Target")))
        {
            var name = (string?)target.Attribute("Name") ?? string.Empty;
            if (string.Equals(name, DependencyTargetName, StringComparison.Ordinal))
                migration.Handled.Add($"<Target Name=\"{name}\"> (ncm embeds the dependencies itself)");
            else if (string.Equals(name, DeployTargetName, StringComparison.Ordinal))
                ReadDeployTarget(target, migration);
            else if (string.Equals(name, RuntimeTargetName, StringComparison.Ordinal))
                migration.Handled.Add(
                    $"<Target Name=\"{name}\"> (ncm resolves the msbuild assemblies from the sdk directory itself)");
            else
                migration.Unhandled.Add($"<Target Name=\"{name}\"> (redo it as a task under <Tasks> if you still need it)");
        }

        return migration;
    }

    //ReadDeployTarget handles the target that copies the output to the host directories
    //Hosts hardcoded in the project carry over, while values passed on the command line cannot be resolved and suggest <Deploy> instead
    private static void ReadDeployTarget(XElement target, Migration migration)
    {
        var hosts = target.Descendants()
            .Where(element => Folded(element, "ModHostDir"))
            .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
            .Where(value => value.Length > 0 && !value.Contains("$(", StringComparison.Ordinal))
            .ToList();

        if (hosts.Count == 0)
        {
            migration.Unhandled.Add(
                $"<Target Name=\"{DeployTargetName}\"> (ncm does the same, list the host mods directories in <Deploy To=\"...\">)");
            return;
        }

        migration.Deploy.AddRange(hosts);
        migration.Migrated.Add($"Deploy {string.Join(';', hosts)}");
    }

    //ReadProperty migrates a recognized property or records the unrecognized one
    private static void ReadProperty(XElement property, Migration migration)
    {
        var name = property.Name.LocalName;
        var value = property.Value.Trim();

        switch (name)
        {
            case "AssemblyName":
                migration.AssemblyName = value;
                migration.Migrated.Add($"AssemblyName {value}");
                break;
            case "RootNamespace":
                migration.RootNamespace = value;
                migration.Migrated.Add($"RootNamespace {value}");
                break;
            case "OutputType":
                if (string.Equals(value, NcBuild.ExeOutputType, StringComparison.OrdinalIgnoreCase))
                {
                    migration.OutputType = NcBuild.ExeOutputType;
                    migration.Migrated.Add($"OutputType {value}");
                }
                else if (string.Equals(value, NcBuild.DefaultOutputType, StringComparison.OrdinalIgnoreCase))
                    migration.Handled.Add($"<OutputType>{value}</OutputType> (the ncm default)");
                else
                    migration.Unhandled.Add($"<OutputType>{value}</OutputType>");
                break;
            case "OutputPath":
                //ncproj only expands its own variables, so msbuild properties would carry over as dead strings
                if (value.Contains("$(", StringComparison.Ordinal))
                    migration.Unhandled.Add($"<OutputPath>{value}</OutputPath> (ncproj does not expand msbuild properties)");
                else
                {
                    migration.Output = value;
                    migration.Migrated.Add($"Output {value}");
                }
                break;
            case "LangVersion":
                migration.LangVersion = value;
                migration.Migrated.Add($"LangVersion {value}");
                break;
            case "DefineConstants":
                migration.DefineConstants = value;
                migration.Migrated.Add($"DefineConstants {value}");
                break;
            case "Nullable":
                if (TryBool(value, out var nullable))
                {
                    migration.Nullable = nullable;
                    migration.Migrated.Add($"Nullable {value}");
                }
                else
                    migration.Unhandled.Add($"<Nullable>{value}</Nullable>");
                break;
            case "AllowUnsafeBlocks":
                if (TryBool(value, out var allowUnsafe))
                {
                    migration.AllowUnsafe = allowUnsafe;
                    migration.Migrated.Add($"AllowUnsafe {value}");
                }
                else
                    migration.Unhandled.Add($"<AllowUnsafeBlocks>{value}</AllowUnsafeBlocks>");
                break;
            case "ImplicitUsings":
                if (TryBool(value, out var implicitUsings))
                {
                    migration.ImplicitUsings = implicitUsings;
                    migration.Migrated.Add($"ImplicitUsings {value}");
                }
                else
                    migration.Unhandled.Add($"<ImplicitUsings>{value}</ImplicitUsings>");
                break;
            case "TargetFramework":
                migration.Handled.Add($"<TargetFramework>{value}</TargetFramework> (ncm follows its own runtime)");
                if (!string.Equals(value, TargetFramework.Name, StringComparison.OrdinalIgnoreCase))
                {
                    migration.Warnings.Add(
                        $"the project targets {value} but ncm runs on {TargetFramework.Name}, the kernel must run on the same runtime");
                }
                break;
            case "PackAsTool":
            case "ToolCommandName":
            case "PackageId":
            case "Description":
            case "PackageLicenseExpression":
                migration.Handled.Add($"<{name}>{value}</{name}> (that belongs to dotnet pack, ncm does not pack the project)");
                break;
            case "EnableDefaultCompileItems":
            case "EnableDefaultItems":
            case "GenerateAssemblyInfo":
                migration.Handled.Add($"<{name}>{value}</{name}> (ncm always does this)");
                break;
            default:
                migration.Unhandled.Add($"<{name}>{value}</{name}>");
                break;
        }
    }

    //ReadItem handles one item; mostly only package references need migrating, the rest is what ncm already does
    private static void ReadItem(XElement item, Migration migration)
    {
        var name = item.Name.LocalName;
        var include = (string?)item.Attribute("Include") ?? (string?)item.Attribute("Update") ?? string.Empty;

        switch (name)
        {
            case "PackageReference":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<PackageReference> without an id");
                    return;
                }

                var version = VersionOf(item);
                migration.Packages.Add(new NcPackage(include, version));
                migration.Migrated.Add($"Package {include} {(version.Length > 0 ? version : "(open)")}");
                return;
            case "Reference":
                if (include.Contains("libs", StringComparison.OrdinalIgnoreCase)
                    || include.Contains("kernel", StringComparison.OrdinalIgnoreCase))
                {
                    migration.Handled.Add(
                        $"<Reference Include=\"{include}\"> (ncm references Build\\kernel automatically, list anything else in that folder under <References>)");
                    return;
                }

                //References with a HintPath use it, since that is the real location of the dll
                var file = HintPath(item);
                var path = file.Length > 0 ? file : include;
                if (string.IsNullOrWhiteSpace(path))
                {
                    migration.Unhandled.Add("<Reference> without a path or a HintPath");
                    return;
                }

                migration.Files.Add(path);
                migration.Migrated.Add($"File {path}");
                return;
            case "ProjectReference":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<ProjectReference> without a path");
                    return;
                }

                migration.Projects.Add(include);
                migration.Migrated.Add($"Project {include}");
                return;
            case "EmbeddedResource":
                if (IsManifestOrIcon(include) || IsManifestOrIcon(LogicalName(item)))
                {
                    migration.Handled.Add($"<EmbeddedResource Include=\"{include}\"> (ncm embeds the manifest and the icon itself)");
                    return;
                }

                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<EmbeddedResource> without an Include");
                    return;
                }

                var logical = LogicalName(item);
                migration.Embedded.Add(new NcResource(include, logical));
                migration.Migrated.Add($"EmbeddedResource {include}"
                    + (logical.Length > 0 ? $" as {logical}" : string.Empty));
                return;
            case "Compile":
                migration.Handled.Add($"<Compile Include=\"{include}\"> (ncm scans the sources itself)");
                return;
            case "AvaloniaXaml":
                migration.Handled.Add($"<AvaloniaXaml Include=\"{include}\"> (ncm picks up every axaml itself)");
                return;
            case "AvaloniaResource":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<AvaloniaResource> without an Include");
                    return;
                }

                migration.AvaloniaResources.Add(include);
                migration.Migrated.Add($"AvaloniaResource {include}");
                return;
            case "InternalsVisibleTo":
                if (string.IsNullOrWhiteSpace(include))
                {
                    migration.Unhandled.Add("<InternalsVisibleTo> without an assembly name");
                    return;
                }

                migration.Friends.Add(include);
                migration.Migrated.Add($"InternalsVisibleTo {include}");
                return;
            default:
                migration.Unhandled.Add($"<{name} Include=\"{include}\">");
                return;
        }
    }

    //Compose assembles the migrated pieces into a .ncproj
    //Defaults are left out to keep the config short, since reading falls back to them anyway
    //What the machine wide config declares is written in as well, so the new project starts from the same source, mirror
    //and compile options as every other one instead of silently depending on a file somewhere else
    private static XDocument Compose(Migration migration, NcProject? global)
    {
        var root = new XElement("ncproj", new XAttribute("version", "1"));

        var sharedCheck = Declares(global, "Check") ? global!.Check : null;
        var sharedBuild = Declares(global, "Build") ? global!.Build : null;
        var langVersion = migration.LangVersion.Length > 0
            ? migration.LangVersion
            : sharedCheck?.LangVersion ?? string.Empty;
        if (langVersion.Length > 0)
            root.Add(new XElement("Check", new XAttribute("LangVersion", langVersion)));

        var build = new XElement("Build");
        SetAttribute(build, "AssemblyName", migration.AssemblyName);
        SetAttribute(build, "RootNamespace", migration.RootNamespace);
        SetAttribute(build, "OutputType", migration.OutputType);
        SetAttribute(build, "Output", migration.Output);
        SetAttribute(build, "DefineConstants", migration.DefineConstants.Length > 0
            ? migration.DefineConstants
            : sharedBuild?.DefineConstants ?? string.Empty);
        SetAttribute(build, "ExtraArgs", sharedBuild?.ExtraArgs ?? string.Empty);
        if ((migration.Nullable ?? sharedBuild?.Nullable) is { } nullable)
            build.SetAttributeValue("Nullable", nullable.ToString().ToLowerInvariant());
        if ((migration.ImplicitUsings ?? sharedBuild?.ImplicitUsings) is { } implicitUsings)
            build.SetAttributeValue("ImplicitUsings", implicitUsings.ToString().ToLowerInvariant());
        //the unsafe switch is written whenever it is known, since turning it on is what a csproj had to say out loud
        if ((migration.AllowUnsafe ?? (sharedBuild?.AllowUnsafe == true ? true : null)) is { } allowUnsafe)
            build.SetAttributeValue("AllowUnsafe", allowUnsafe.ToString().ToLowerInvariant());
        if (sharedBuild is { DependsOnModApi: false })
            build.SetAttributeValue("DependsOnModApi", "false");
        if (build.HasAttributes)
            root.Add(build);

        if (migration.Packages.Count > 0)
        {
            var packages = new XElement("Packages");
            foreach (var package in migration.Packages)
            {
                var item = new XElement("Package", new XAttribute("Id", package.Id));
                SetAttribute(item, "Version", package.Version);
                packages.Add(item);
            }
            root.Add(packages);
        }

        if (migration.Files.Count > 0 || migration.Projects.Count > 0)
        {
            var references = new XElement("References");
            foreach (var include in migration.Files)
                references.Add(new XElement("File", new XAttribute("Include", include)));
            foreach (var include in migration.Projects)
                references.Add(new XElement("Project", new XAttribute("Include", include)));
            root.Add(references);
        }

        Share(root, global);

        if (migration.Friends.Count > 0)
        {
            var friends = new XElement("InternalsVisibleTo");
            foreach (var name in migration.Friends)
                friends.Add(new XElement("Assembly", new XAttribute("Name", name)));
            root.Add(friends);
        }

        if (migration.Deploy.Count > 0)
            root.Add(new XElement("Deploy", new XAttribute("To", string.Join(';', migration.Deploy))));

        if (migration.Embedded.Count > 0)
        {
            var embedded = new XElement("EmbeddedResources");
            foreach (var resource in migration.Embedded)
            {
                var item = new XElement("Resource", new XAttribute("Include", resource.Include));
                SetAttribute(item, "LogicalName", resource.LogicalName);
                embedded.Add(item);
            }
            root.Add(embedded);
        }

        if (migration.AvaloniaResources.Count > 0)
        {
            var resources = new XElement("AvaloniaResources");
            foreach (var include in migration.AvaloniaResources)
                resources.Add(new XElement("Resource", new XAttribute("Include", include)));
            root.Add(resources);
        }

        return new XDocument(root);
    }

    //Write the pull rules of the machine wide config into the new project, so the migrated project is readable on its own
    //and does not start behaving differently the day the global file changes
    private static void Share(XElement root, NcProject? global)
    {
        if (global is null)
            return;

        if (global.Sources is { } sources)
        {
            var element = new XElement("Sources");
            SetAttribute(element, "Directory", sources.Directory);
            SetAttribute(element, "Url", sources.Url);
            if (sources.Index != NcSource.DefaultIndex)
                element.SetAttributeValue("Index", sources.Index);
            if (sources.Format != NcSource.DefaultFormat)
                element.SetAttributeValue("Format", sources.Format);
            SetAttribute(element, "Pattern", sources.Pattern);
            root.Add(element);
        }

        if (Declares(global, "Server"))
        {
            var element = new XElement("Server");
            SetAttribute(element, "Cache", global.Server.Cache);
            SetAttribute(element, "Args", global.Server.Args);
            if (global.Server.Debug)
                element.SetAttributeValue("Debug", "true");
            root.Add(element);
        }

        if (Declares(global, "Client"))
        {
            var element = new XElement("Client");
            SetAttribute(element, "Version", global.Client.Version);
            SetAttribute(element, "Jar", global.Client.Jar);
            SetAttribute(element, "Args", global.Client.Args);
            root.Add(element);
        }

        if (global.Template is { } template)
        {
            var element = new XElement("Template");
            SetAttribute(element, "Url", template.Url);
            SetAttribute(element, "Base", template.Base);
            root.Add(element);
        }
    }

    //Declares tells whether the machine wide config carries an element at all, an absent one meaning it has no opinion
    private static bool Declares(NcProject? global, string element)
        => global is not null && global.Declared.Contains(element);

    //Report prints what the migration did, what ncm takes over and what was not migrated
    private static void Report(Migration migration, string source, string target, bool noBackup)
    {
        Console.WriteLine($"Written {Path.GetFileName(target)}");
        Write("Migrated:", migration.Migrated, ConsoleColor.Green);
        Write("Handled by ncm, dropped:", migration.Handled, ConsoleColor.DarkGray);
        Write("Not migrated, check them yourself:", migration.Unhandled, ConsoleColor.Yellow);
        Write("Warnings:", migration.Warnings, ConsoleColor.Yellow);

        Console.WriteLine(noBackup
            ? $"Removed {Path.GetFileName(source)}"
            : $"Backed up the original to {Path.GetFileName(source + BackupExtension)}");
    }

    //Write prints a titled section and skips it entirely when empty
    private static void Write(string title, List<string> lines, ConsoleColor color)
    {
        if (lines.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine(title);
        Console.ForegroundColor = color;
        foreach (var line in lines)
            Console.WriteLine($"  {line}");
        Console.ResetColor();
    }

    //VersionOf reads the package version from either the attribute or the child element
    private static string VersionOf(XElement item)
        => (string?)item.Attribute("Version")
            ?? item.Elements().FirstOrDefault(element => Folded(element, "Version"))?.Value.Trim()
            ?? string.Empty;

    //LogicalName is the hardcoded resource name of an embedded resource
    private static string LogicalName(XElement item)
        => (string?)item.Attribute("LogicalName")
            ?? item.Elements().FirstOrDefault(element => Folded(element, "LogicalName"))?.Value.Trim()
            ?? string.Empty;

    //HintPath is the real location of an assembly reference, empty when unset
    private static string HintPath(XElement item)
        => item.Elements().FirstOrDefault(element => Folded(element, "HintPath"))?.Value.Trim() ?? string.Empty;

    //IsManifestOrIcon tells whether the resource is the manifest or the icon, which ncm embeds itself
    private static bool IsManifestOrIcon(string value)
        => value.Contains(ModProject.ManifestName, StringComparison.OrdinalIgnoreCase)
            || value.Contains(ModProject.DefaultIconName, StringComparison.OrdinalIgnoreCase);

    //SetAttribute writes the attribute only when there is a value
    private static void SetAttribute(XElement element, string name, string value)
    {
        if (value.Length > 0)
            element.SetAttributeValue(name, value);
    }

    //TryBool accepts both the true/false and the enable/disable forms from csproj
    private static bool TryBool(string value, out bool result)
    {
        if (bool.TryParse(value, out result))
            return true;

        if (string.Equals(value, "enable", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        if (string.Equals(value, "disable", StringComparison.OrdinalIgnoreCase))
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    //Folded compares element names case-insensitively
    private static bool Folded(XElement element, string name)
        => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    //Migration collects the results of one conversion
    private sealed class Migration
    {
        public string AssemblyName { get; set; } = string.Empty;

        //RootNamespace only affects the default names of embedded resources
        public string RootNamespace { get; set; } = string.Empty;

        //OutputType only ever holds Exe or the default Library
        public string OutputType { get; set; } = string.Empty;

        public string Output { get; set; } = string.Empty;

        public string LangVersion { get; set; } = string.Empty;

        public string DefineConstants { get; set; } = string.Empty;

        //null means the project does not set it, so it is left out of the ncproj and the default applies
        public bool? Nullable { get; set; }

        public bool? ImplicitUsings { get; set; }

        //null means the project does not set it, so the compiler default of no unsafe code applies
        public bool? AllowUnsafe { get; set; }

        public List<NcPackage> Packages { get; } = [];

        //Friends are the assemblies that get access to internal members
        public List<string> Friends { get; } = [];

        //Deploy is where the build output is copied after building
        public List<string> Deploy { get; } = [];

        //AvaloniaResources are the patterns packed into the resource bundle
        public List<string> AvaloniaResources { get; } = [];

        //Embedded are the plain resources embedded into the output assembly
        public List<NcResource> Embedded { get; } = [];

        //Files are the dlls used directly as compile references
        public List<string> Files { get; } = [];

        //Projects are the other projects whose output is referenced
        public List<string> Projects { get; } = [];

        //Migrated lists what actually carried over
        public List<string> Migrated { get; } = [];

        //Handled lists what ncm does itself, so dropping it is harmless
        public List<string> Handled { get; } = [];

        //Unhandled lists what needs manual attention
        public List<string> Unhandled { get; } = [];

        //Warnings lists what carried over but deserves a note
        public List<string> Warnings { get; } = [];
    }
}

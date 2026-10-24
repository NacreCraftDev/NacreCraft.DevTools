using System.Xml;
using System.Xml.Linq;

namespace NacreCraft.ModBuild.Core;

//C# syntax check settings for the project
public sealed class NcCheck
{
    //Language version used when the config omits one
    public const string DefaultLangVersion = "latest";

    public NcCheck(string? langVersion)
    {
        LangVersion = string.IsNullOrWhiteSpace(langVersion) ? DefaultLangVersion : langVersion;
    }

    //C# language version used to parse the sources
    public string LangVersion { get; }
}

//Build settings for the project
public sealed class NcBuild
{
    //Configuration used when the config omits one
    public const string DefaultConfiguration = "Release";

    //Default output directory, relative to the project root
    public static readonly string DefaultOutput = ProjectLayout.Output;

    //Output type used when the config omits one, which is what every mod uses
    public const string DefaultOutputType = "Library";

    //Output type name for a console executable
    public const string ExeOutputType = "Exe";

    public static readonly string[] OutputTypes = [DefaultOutputType, ExeOutputType];

    //Whether a project depends on the mod api when the config omits the switch
    //Every template mod does, and only a pure mod that talks to the kernel directly can turn it off
    public const bool DefaultDependsOnModApi = true;

    //Whether unsafe code is allowed when the config omits the switch
    //Off by default, since the kernel runs every mod in its own process and an unchecked pointer takes the server with it
    public const bool DefaultAllowUnsafe = false;

    public NcBuild(string assemblyName, string configuration, string output, string extraArgs, bool nullable,
        bool implicitUsings, string defineConstants, string outputType, string rootNamespace, bool dependsOnModApi,
        bool allowUnsafe)
    {
        AssemblyName = assemblyName;
        Configuration = string.IsNullOrWhiteSpace(configuration) ? DefaultConfiguration : configuration;
        Output = string.IsNullOrWhiteSpace(output) ? DefaultOutput : output;
        ExtraArgs = extraArgs;
        Nullable = nullable;
        ImplicitUsings = implicitUsings;
        DefineConstants = defineConstants;
        OutputType = Normalized(outputType);
        RootNamespace = rootNamespace;
        DependsOnModApi = dependsOnModApi;
        AllowUnsafe = allowUnsafe;
    }

    //Assembly name, empty falls back to the manifest id and then to the directory name
    public string AssemblyName { get; }

    //Build configuration name
    public string Configuration { get; }

    //Output directory, relative to the project root
    public string Output { get; }

    //Extra arguments passed to the compiler invocation
    public string ExtraArgs { get; }

    //Nullable reference type checking
    public bool Nullable { get; }

    //Implicit usings
    public bool ImplicitUsings { get; }

    //Compile-time symbols, semicolon separated
    public string DefineConstants { get; }

    //Product kind, see OutputTypes
    public string OutputType { get; }

    //Root namespace, empty follows the assembly name and only affects the default resource names
    public string RootNamespace { get; }

    //Whether the mod api is restored as a compile reference and staged next to the mod
    //Turn it off for a pure mod that does not use the api, so ncm never pulls that assembly in
    public bool DependsOnModApi { get; }

    //Whether unsafe code is allowed, the equivalent of the csproj AllowUnsafeBlocks property
    public bool AllowUnsafe { get; }

    //Normalize an output type ignoring case, falling back to the library type
    private static string Normalized(string value)
        => OutputTypes.FirstOrDefault(type => string.Equals(type, value, StringComparison.OrdinalIgnoreCase))
            ?? DefaultOutputType;
}

//One package dependency
public sealed record NcPackage(string Id, string Version);

//A resource embedded into the output assembly
//The logical name defaults to the root namespace plus the relative path
public sealed record NcResource(string Include, string LogicalName);

//Development-time server settings
//Cache sets the runtime file cache location, Args are applied on every launch and Debug keeps debug mode always on
public sealed class NcServer
{
    public NcServer(string cache, string args, bool debug)
    {
        Cache = cache;
        Args = args;
        Debug = debug;
    }

    //Runtime cache directory, empty uses the one bundled with ncm
    public string Cache { get; }

    //Arguments applied on every launch, whitespace separated with double quotes around values containing spaces
    public string Args { get; }

    //Keeps debug mode always on, same as the --debug command line flag
    public bool Debug { get; }
}

//Development-time client settings
//The client launcher is not implemented yet, these are parsed ahead of time
public sealed class NcClient
{
    public NcClient(string args, string version, string jar)
    {
        Args = args;
        Version = version;
        Jar = jar;
    }

    //Arguments applied when launching the client
    public string Args { get; }

    //Client jar version, empty follows the kernel version
    public string Version { get; }

    //Direct download URL for the client jar, which skips the version manifest
    public string Jar { get; }
}

//Kernel source and index parsing rules
//Directory is a local kernel directory standing in for the cache, Url the base address, Index the index file name,
//Format how the index is read and Pattern only applies to the regex format
public sealed class NcSource
{
    //Default format reads one hash and path per line like sha256sum output
    public const string DefaultFormat = "sha256-lines";

    //Default index file name
    public const string DefaultIndex = "index.txt";

    //Index formats the loader understands
    public static readonly string[] Formats = [DefaultFormat, "plain", "regex"];

    public NcSource(string directory, string url, string index, string format, string pattern)
    {
        Directory = directory;
        Url = url;
        Index = string.IsNullOrWhiteSpace(index) ? DefaultIndex : index;
        Format = string.IsNullOrWhiteSpace(format) ? DefaultFormat : format;
        Pattern = pattern;
    }

    //Local kernel directory, relative to the project root, taking the place of the cache when it exists
    //An empty value means the kernel is downloaded into the cache
    public string Directory { get; }

    //Download base address for kernel files, empty uses the built-in one
    public string Url { get; }

    //Index file name, relative to the base address
    public string Index { get; }

    //Index parsing format, see Formats
    public string Format { get; }

    //Pattern for the regex format, whose two capture groups are the hash and the relative path
    public string Pattern { get; }
}

//Source of the template directory
//Url is the index address and Base the sample file address, both accepting a mirror prefix
//Base defaults to whatever the index declares
public sealed class NcTemplate
{
    public NcTemplate(string url, string baseUrl)
    {
        Url = url;
        Base = baseUrl;
    }

    //Index address, empty uses the built-in one
    public string Url { get; }

    //Sample file base address, empty uses the one from the index
    public string Base { get; }
}

//Kind of a task step
public enum NcStepKind
{
    Exec,
    Copy,
    Zip,
    Tool,
    Script,
}

//A step inside a task
//Exec uses Command, Copy and Zip use From and To, Tool uses Name, Method and Args, leaving the unused fields empty
public sealed class NcStep
{
    private NcStep(NcStepKind kind, string command, string from, string to, string name, string method, string args,
        string source = "", string file = "")
    {
        Kind = kind;
        Command = command;
        From = from;
        To = to;
        Name = name;
        Method = method;
        Args = args;
        Source = source;
        File = file;
    }

    public static NcStep Exec(string command)
        => new(NcStepKind.Exec, command, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    public static NcStep Copy(string from, string to)
        => new(NcStepKind.Copy, string.Empty, from, to, string.Empty, string.Empty, string.Empty);

    public static NcStep Zip(string from, string to)
        => new(NcStepKind.Zip, string.Empty, from, to, string.Empty, string.Empty, string.Empty);

    public static NcStep Tool(string name, string method, string args)
        => new(NcStepKind.Tool, string.Empty, string.Empty, string.Empty, name, method, args);

    public static NcStep Script(string source, string file)
        => new(NcStepKind.Script, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            source, file);

    //Kind of this step
    public NcStepKind Kind { get; }

    //Command line for the Exec kind
    public string Command { get; }

    //Source, relative to the project root
    public string From { get; }

    //Destination, relative to the project root
    public string To { get; }

    //Plugin name for the Tool kind
    public string Name { get; }

    //Entry point for the Tool kind
    public string Method { get; }

    //Arguments for the Tool kind, split the same way as the server arguments
    public string Args { get; }

    //Script source for the Script kind, already read from File when one was named
    public string Source { get; }

    //Script file relative to the project root, empty for an inline script
    public string File { get; }

    //Text used for display and fingerprinting
    public string Text => Kind switch
    {
        NcStepKind.Copy => $"copy {From} -> {To}",
        NcStepKind.Zip => $"zip {From} -> {To}",
        NcStepKind.Tool => $"tool {Name} {Method} {Args}".TrimEnd(),
        NcStepKind.Script => string.IsNullOrEmpty(File) ? "script inline" : $"script {File}",
        _ => Command,
    };
}

//A task declared in the project config
//Steps run in order, with dependencies completed first
public sealed class NcTask
{
    //Description shown in help and listings when a task omits one
    public const string DefaultDescription = "Project custom task";

    public NcTask(string name, string description, IReadOnlyList<string> depends, bool overrides,
        IReadOnlyList<NcStep> steps)
    {
        Name = name;
        Description = description;
        Depends = depends;
        Overrides = overrides;
        Steps = steps;
    }

    //Task name, used to invoke it from the command line
    public string Name { get; }

    //One-line description from the config, empty when omitted
    public string Description { get; }

    //Names of tasks that must finish first
    public IReadOnlyList<string> Depends { get; }

    //Whether this task may override a built-in tool of the same name
    public bool Overrides { get; }

    //Steps executed in order
    public IReadOnlyList<NcStep> Steps { get; }

    //Display description, falling back to the default text
    public string Title => string.IsNullOrWhiteSpace(Description) ? DefaultDescription : Description;
}

//NcScopedTask one task reached through the namespace of a bootstrap config
public sealed record NcScopedTask(NcProject Project, NcTask Task, string Name);

//The .ncproj development config of a project
//Holds development-time items such as tasks, run arguments and sources, and is never shipped with a mod
//Distinct from ncmod.json, the mod data manifest embedded into the dll
public sealed class NcProject
{
    public const string Extension = ".ncproj";

    //Config version this build understands
    private const string SupportedVersion = "1";

    private NcProject(string path, bool overrides, NcCheck check, NcBuild build,
        IReadOnlyList<NcPackage> packages, IReadOnlyList<NcTask> tasks, NcServer server, NcClient client,
        NcSource? sources, NcTemplate? template, IReadOnlyList<string> friends, IReadOnlyList<string> deploy,
        IReadOnlyList<string> resources, IReadOnlyList<NcResource> embedded, IReadOnlyList<string> files,
        IReadOnlyList<string> projects, IReadOnlyList<string> exclude, IReadOnlySet<string> declared,
        bool allowScriptClr, bool bootstrap)
    {
        Path = path;
        Directory = System.IO.Path.GetDirectoryName(path)!;
        Overrides = overrides;
        Check = check;
        Build = build;
        Packages = packages;
        Tasks = tasks;
        Server = server;
        Client = client;
        Sources = sources;
        Template = template;
        InternalsVisibleTo = friends;
        DeployTargets = deploy;
        AvaloniaResources = resources;
        EmbeddedResources = embedded;
        FileReferences = files;
        ProjectReferences = projects;
        Exclude = exclude;
        Declared = declared;
        AllowScriptClr = allowScriptClr;
        Bootstrap = bootstrap;
    }

    //Full path of the config file
    public string Path { get; }

    //Directory containing the config file, which is the project root
    public string Directory { get; }

    //Global default applied to tasks that do not set Override themselves
    public bool Overrides { get; }

    //Whether task scripts may reach the CLR, off unless <Tasks AllowClr="true"> asks for it
    public bool AllowScriptClr { get; }

    //Whether this config only aggregates other projects and owns no sources of its own
    public bool Bootstrap { get; }

    //Syntax check settings
    public NcCheck Check { get; }

    //Build settings
    public NcBuild Build { get; }

    //AssemblyNameOf the compiled output: the configured name, or the project folder name when the config leaves it out
    //The folder name is what the template used, so an older artifact keeps its name
    public string AssemblyNameOf(string root)
        => string.IsNullOrWhiteSpace(Build.AssemblyName)
            ? new DirectoryInfo(root).Name
            : Build.AssemblyName;

    //Declared package dependencies
    public IReadOnlyList<NcPackage> Packages { get; }

    //All tasks defined in the config, including those shadowed by built-ins
    public IReadOnlyList<NcTask> Tasks { get; }

    //Development-time server settings
    public NcServer Server { get; }

    //Development-time client settings
    public NcClient Client { get; }

    //Kernel source, null uses the built-in one
    public NcSource? Sources { get; }

    //Template directory source, null uses the built-in one
    public NcTemplate? Template { get; }

    //Assemblies allowed to access internal members
    public IReadOnlyList<string> InternalsVisibleTo { get; }

    //Directories the build output is copied to, relative to the project root or absolute
    public IReadOnlyList<string> DeployTargets { get; }

    //Patterns kept out of everything ncm scans on its own: the compile source list, the checks and the resource pick up
    //Patterns explicitly declared elsewhere are still honored, so an excluded directory can still be referenced on purpose
    public IReadOnlyList<string> Exclude { get; }

    //Patterns packed into !AvaloniaResources, relative to the project root
    public IReadOnlyList<string> AvaloniaResources { get; }

    //Plain resources embedded into the output assembly
    public IReadOnlyList<NcResource> EmbeddedResources { get; }

    //dll patterns referenced directly at compile time, relative to the project root and supporting wildcards
    public IReadOnlyList<string> FileReferences { get; }

    //Other projects whose output is referenced, relative to the project root and either a directory or a project file
    public IReadOnlyList<string> ProjectReferences { get; }

    //Names of the elements this config declares, used to tell apart an unset value from a deliberate one
    public IReadOnlySet<string> Declared { get; }

    //Fill in what this config leaves out from a machine wide one
    //A value declared here always wins, since a project is the more specific of the two; an element that is absent uses
    //the global one whole, while the pull rules fall back field by field so a half written <Server> still keeps the rest
    //Relative paths of the global config stay relative to it, so a shared <Deploy> or <References> has to be absolute
    internal NcProject InheritFrom(NcProject global)
    {
        var absent = (string element) => !Declared.Contains(element);
        var shared = (string value, string fallback) => value.Length > 0 ? value : fallback;

        var server = new NcServer(shared(Server.Cache, global.Server.Cache), shared(Server.Args, global.Server.Args),
            absent("Server") ? global.Server.Debug : Server.Debug);
        var client = new NcClient(shared(Client.Args, global.Client.Args),
            shared(Client.Version, global.Client.Version), shared(Client.Jar, global.Client.Jar));

        //The pull rules fall back field by field, so a project that writes only its own mirror still takes the kernel
        //directory, the index rules and the rest from the machine; the defaults of Index and Format count as unset
        var sources = Sources ?? global.Sources;
        if (Sources is { } mine && global.Sources is { } theirs)
        {
            sources = new NcSource(shared(mine.Directory, theirs.Directory), shared(mine.Url, theirs.Url),
                mine.Index != NcSource.DefaultIndex ? mine.Index : theirs.Index,
                mine.Format != NcSource.DefaultFormat ? mine.Format : theirs.Format,
                shared(mine.Pattern, theirs.Pattern));
        }

        var template = Template ?? global.Template;
        if (Template is { } mineTemplate && global.Template is { } theirTemplate)
        {
            template = new NcTemplate(shared(mineTemplate.Url, theirTemplate.Url),
                shared(mineTemplate.Base, theirTemplate.Base));
        }

        return new NcProject(Path, Overrides, absent("Check") ? global.Check : Check,
            absent("Build") ? global.Build : Build, absent("Packages") ? global.Packages : Packages, Tasks,
            server, client, sources, template,
            InternalsVisibleTo.Count > 0 ? InternalsVisibleTo : global.InternalsVisibleTo,
            DeployTargets.Count > 0 ? DeployTargets : global.DeployTargets,
            AvaloniaResources.Count > 0 ? AvaloniaResources : global.AvaloniaResources,
            EmbeddedResources.Count > 0 ? EmbeddedResources : global.EmbeddedResources,
            FileReferences.Count > 0 ? FileReferences : global.FileReferences,
            ProjectReferences.Count > 0 ? ProjectReferences : global.ProjectReferences,
            Exclude.Count > 0 ? Exclude : global.Exclude, Declared,
            AllowScriptClr || global.AllowScriptClr, Bootstrap);
    }

    //Search upward from the given directory for a project config, returning null when none is found
    //A non-empty error means one was found but could not be read, which is different from having none
    public static NcProject? TryFind(string directory, out string error)
    {
        error = string.Empty;
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var candidate = FindIn(current.FullName);
            if (candidate is not null)
            {
                Trace.Log($"found project config {candidate}");
                //Every caller sees the same effective config, so the machine wide defaults are applied right here rather
                //than at each tool's own lookup
                var project = Load(candidate, out error);
                var global = GlobalConfig.Load();
                return project is not null && global is not null ? project.InheritFrom(global) : project;
            }
        }

        Trace.Log($"no {Extension} from {directory} upward");
        return null;
    }

    //A bootstrap may only aggregate real projects; nesting another bootstrap would create two competing namespaces
    private static bool ValidateBootstrapReferences(string path, IReadOnlyList<string> includes, out string error)
    {
        error = string.Empty;
        var root = System.IO.Path.GetDirectoryName(path);
        if (root is null)
            return true;

        foreach (var include in includes)
        {
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, include));
            var directory = System.IO.Directory.Exists(target) ? target : System.IO.Path.GetDirectoryName(target);
            if (directory is null || !System.IO.Directory.Exists(directory))
                continue;

            var config = System.IO.File.Exists(target) && target.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                ? target
                : FindIn(directory);
            if (config is null || string.Equals(config, path, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsBootstrapConfig(config))
                continue;

            error = $"{path} is a bootstrap config and cannot reference another bootstrap config: {config}";
            return false;
        }

        return true;
    }

    private static bool IsBootstrapConfig(string path)
    {
        try
        {
            var root = XDocument.Load(path).Root;
            return string.Equals((string?)root?.Attribute("Bootstrap"), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    //Find a config in the directory, preferring the one matching the directory name when several exist
    private static string? FindIn(string directory)
    {
        var files = System.IO.Directory.EnumerateFiles(directory, "*" + Extension).ToList();
        if (files.Count == 0)
            return null;

        if (files.Count == 1)
            return files[0];

        var preferred = System.IO.Path.Combine(directory, new DirectoryInfo(directory).Name + Extension);
        return files.FirstOrDefault(file => string.Equals(file, preferred, StringComparison.OrdinalIgnoreCase))
            ?? files[0];
    }

    //Add a package dependency, replacing the version when the id already exists, and return whether it succeeded
    //Only the <Packages> element is touched, leaving every other node unchanged
    public bool AddPackage(string id, string version, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(Path);
        }
        catch (XmlException e)
        {
            error = $"{Path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{Path} has no root element";
            return false;
        }

        var packages = root.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "Packages"));
        if (packages is null)
        {
            packages = new XElement("Packages");
            root.Add(packages);
        }

        var existing = packages.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "Package")
            && string.Equals((string?)element.Attribute("Id"), id, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var package = new XElement("Package", new XAttribute("Id", id));
            if (!string.IsNullOrWhiteSpace(version))
                package.SetAttributeValue("Version", version);
            packages.Add(package);
        }
        //A missing version leaves the existing entry untouched, matching the rule that a blank field never changes anything
        else if (!string.IsNullOrWhiteSpace(version))
        {
            existing.SetAttributeValue("Version", version);
        }

        try
        {
            XmlFile.Save(Path, document);
        }
        catch (IOException e)
        {
            error = $"cannot write {Path}: {e.Message}";
            return false;
        }

        return true;
    }

    //Remove a package dependency from <Packages>, leaving an empty element behind
    public bool RemovePackage(string id, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(Path);
        }
        catch (XmlException e)
        {
            error = $"{Path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{Path} has no root element";
            return false;
        }

        var packages = root.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "Packages"));
        var existing = packages?.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "Package")
            && string.Equals((string?)element.Attribute("Id"), id, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            error = $"{id} is not declared in {Path}";
            return false;
        }

        existing.Remove();
        return Save(document, out error);
    }

    //SetDependsOnModApi writes the mod api switch into <Build>, creating the element when the config has none
    //Keeping the element means a project that never declared it ends up with an explicit value rather than an empty node
    public bool SetDependsOnModApi(bool depends, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(Path);
        }
        catch (XmlException e)
        {
            error = $"{Path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{Path} has no root element";
            return false;
        }

        var build = root.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "Build"));
        if (build is null)
        {
            build = new XElement("Build");
            root.Add(build);
        }

        build.SetAttributeValue("DependsOnModApi", depends ? "true" : "false");
        return Save(document, out error);
    }

    //Save writes the edited config back, reporting a failure instead of throwing
    private bool Save(XDocument document, out string error)
    {
        error = string.Empty;
        try
        {
            XmlFile.Save(Path, document);
            return true;
        }
        catch (IOException e)
        {
            error = $"cannot write {Path}: {e.Message}";
            return false;
        }
    }

    //Add a reference, skipping an existing one with the same kind and path, and return whether it succeeded
    //isProject writes into <Project> and otherwise into <File>
    public bool AddReference(string include, bool isProject, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(Path);
        }
        catch (XmlException e)
        {
            error = $"{Path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{Path} has no root element";
            return false;
        }

        var references = root.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, "References"));
        if (references is null)
        {
            references = new XElement("References");
            root.Add(references);
        }

        var name = isProject ? "Project" : "File";
        var relative = ToRelative(include);
        var existing = references.Elements().FirstOrDefault(element => Folded(element.Name.LocalName, name)
            && string.Equals((string?)element.Attribute("Include"), relative, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
            references.Add(new XElement(name, new XAttribute("Include", relative)));

        try
        {
            XmlFile.Save(Path, document);
        }
        catch (IOException e)
        {
            error = $"cannot write {Path}: {e.Message}";
            return false;
        }

        return true;
    }

    //Convert a path to be relative to the project root
    //Wildcards are left as is because they are not a resolvable path in the first place
    private string ToRelative(string include)
    {
        var normalized = include.Trim().Replace('\\', '/');
        if (normalized.IndexOfAny(['*', '?']) >= 0)
            return normalized.Replace('/', System.IO.Path.DirectorySeparatorChar);

        var absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, normalized));
        return System.IO.Path.GetRelativePath(Directory, absolute);
    }

    //Find a runnable task by name, case sensitively, returning null when there is none
    //Tasks shadowed by a built-in tool are excluded so callers cannot reach them
    public NcTask? FindTask(string name)
        => Tasks.FirstOrDefault(task => !IsShadowed(task)
            && string.Equals(task.Name, name, StringComparison.Ordinal));

    //ToolPrefix is the namespace a bootstrap gives the tasks of the projects it aggregates
    public string ToolPrefix => Bootstrap ? System.IO.Path.GetFileNameWithoutExtension(Path) : string.Empty;

    //ReferencedConfigs the projects a bootstrap aggregates, resolved once per config
    public IReadOnlyList<NcProject> ReferencedConfigs()
    {
        if (!Bootstrap)
            return [];

        if (_referenced is not null)
            return _referenced;

        var result = new List<NcProject>();
        foreach (var include in ProjectReferences)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, include));
            var directory = System.IO.Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
            if (directory is null)
                continue;

            var config = TryFind(directory, out _);
            if (config is null || string.Equals(config.Path, Path, StringComparison.OrdinalIgnoreCase))
                continue;

            if (result.Any(item => string.Equals(item.Path, config.Path, StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(config);
        }

        _referenced = result;
        return result;
    }

    //ScopedTasks the runnable tasks of every aggregated project, named with the bootstrap prefix
    public IReadOnlyList<NcScopedTask> ScopedTasks()
    {
        if (!Bootstrap)
            return [];

        var prefix = ToolPrefix;
        var result = new List<NcScopedTask>();
        foreach (var child in ReferencedConfigs())
        {
            foreach (var task in child.Runnable())
                result.Add(new NcScopedTask(child, task, prefix + "/" + task.Name));
        }

        return result;
    }

    //FindScopedTask resolves a name such as xxx/build to the project that declares it
    public NcScopedTask? FindScopedTask(string name)
    {
        if (!Bootstrap)
            return null;

        var slash = name.IndexOf('/');
        if (slash <= 0 || slash == name.Length - 1)
            return null;

        if (!string.Equals(name[..slash], ToolPrefix, StringComparison.Ordinal))
            return null;

        var tool = name[(slash + 1)..];
        foreach (var child in ReferencedConfigs())
        {
            if (child.FindTask(tool) is { } task)
                return new NcScopedTask(child, task, name);
        }

        return null;
    }

    //The projects a bootstrap has resolved, kept because the task list asks for them repeatedly
    private IReadOnlyList<NcProject>? _referenced;

    //Tasks defined by this project that can run, excluding those shadowed by built-ins
    public IReadOnlyList<NcTask> Runnable()
        => Tasks.Where(task => !IsShadowed(task)).ToList();

    //Tasks ignored because their name collides with a built-in tool
    public IReadOnlyList<NcTask> Shadowed()
        => Tasks.Where(IsShadowed).ToList();

    //Whether this task is shadowed by a built-in tool of the same name
    //Without Override it is ignored, with Override it replaces the built-in
    public bool IsShadowed(NcTask task)
        => !task.Overrides && IsBuiltInName(task.Name);

    //Whether a built-in tool has this name, compared case sensitively
    public static bool IsBuiltInName(string name)
        => ToolRegistry.All.Any(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

    //Read a config, writing the reason into error when it cannot be loaded
    private static NcProject? Load(string path, out string error)
    {
        error = string.Empty;
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException e)
        {
            error = $"{path} is not valid xml: {e.Message}";
            return null;
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ncproj", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{path} must have a <ncproj> root element";
            return null;
        }

        var version = (string?)root.Attribute("version");
        if (!string.IsNullOrEmpty(version) && !string.Equals(version, SupportedVersion, StringComparison.Ordinal))
            Warn($"{path} declares version {version}, this ncm only knows {SupportedVersion}");

        var overrides = ReadBool(root, "Override", false, path);
        var tasks = new List<NcTask>();
        var packages = new List<NcPackage>();
        var check = new NcCheck(string.Empty);
        var build = new NcBuild(string.Empty, string.Empty, string.Empty, string.Empty, true, true, string.Empty,
            string.Empty, string.Empty, NcBuild.DefaultDependsOnModApi, NcBuild.DefaultAllowUnsafe);
        var server = new NcServer(string.Empty, string.Empty, false);
        var client = new NcClient(string.Empty, string.Empty, string.Empty);
        NcSource? sources = null;
        NcTemplate? template = null;
        var friends = new List<string>();
        var deploy = new List<string>();
        var exclude = new List<string>();
        var resources = new List<string>();
        var embedded = new List<NcResource>();
        var files = new List<string>();
        var projects = new List<string>();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowScriptClr = false;
        var bootstrap = ReadBool(root, "Bootstrap", false, path);

        foreach (var element in root.Elements())
        {
            var name = element.Name.LocalName;
            declared.Add(name);
            if (Folded(name, "Tasks"))
            {
                allowScriptClr = ReadBool(element, "AllowClr", false, path);
                ReadTasks(element, overrides, path, tasks);
            }
            else if (Folded(name, "Check"))
                check = ReadCheck(element);
            else if (Folded(name, "Build"))
                build = ReadBuild(element, path);
            else if (Folded(name, "Packages"))
                ReadPackages(element, path, packages);
            else if (Folded(name, "Server"))
                server = ReadServer(element, path);
            else if (Folded(name, "Client"))
                client = ReadClient(element);
            else if (Folded(name, "Sources"))
                sources = ReadSources(element, path);
            else if (Folded(name, "Template"))
                template = ReadTemplate(element, path);
            else if (Folded(name, "InternalsVisibleTo"))
                ReadFriends(element, path, friends);
            else if (Folded(name, "Deploy"))
                deploy.AddRange(Split((string?)element.Attribute("To"), ';'));
            else if (Folded(name, "Exclude"))
                exclude.AddRange(Split((string?)element.Attribute("Include"), ';'));
            else if (Folded(name, "AvaloniaResources"))
                ReadResources(element, path, resources);
            else if (Folded(name, "EmbeddedResources"))
                ReadEmbeddedResources(element, path, embedded);
            else if (Folded(name, "References"))
                ReadReferences(element, path, files, projects);
            else
                Warn($"unknown element <{name}> in {path}, ignored");
        }

        if (bootstrap && !ValidateBootstrapReferences(path, projects, out error))
            return null;

        return new NcProject(path, overrides, check, build, packages, tasks, server, client, sources,
            template, friends, deploy, resources, embedded, files, projects, exclude, declared, allowScriptClr,
            bootstrap);
    }

    //Read a config from a known path, for the machine wide one that no search should ever reach
    internal static NcProject? LoadFile(string path, out string error)
        => Load(path, out error);

    //Read each embedded resource under <EmbeddedResources>
    //LogicalName is optional and defaults to the root namespace plus the relative path
    private static void ReadEmbeddedResources(XElement parent, string path, List<NcResource> resources)
    {
        foreach (var element in parent.Elements())
        {
            if (!Folded(element.Name.LocalName, "Resource"))
            {
                Warn($"unknown element <{element.Name.LocalName}> under <EmbeddedResources> in {path}, ignored");
                continue;
            }

            var include = (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(include))
            {
                Warn($"a <Resource> under <EmbeddedResources> in {path} has no Include, skipped");
                continue;
            }

            resources.Add(new NcResource(include, (string?)element.Attribute("LogicalName") ?? string.Empty));
        }
    }

    //Read the two kinds of reference under <References>
    //File is a dll or pattern and Project another project, both relative to the project root
    private static void ReadReferences(XElement parent, string path, List<string> files, List<string> projects)
    {
        foreach (var element in parent.Elements())
        {
            var include = (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(include))
            {
                Warn($"a <{element.Name.LocalName}> under <References> in {path} has no Include, skipped");
                continue;
            }

            if (Folded(element.Name.LocalName, "File"))
                files.Add(include);
            else if (Folded(element.Name.LocalName, "Project"))
                projects.Add(include);
            else
                Warn($"unknown element <{element.Name.LocalName}> under <References> in {path}, ignored");
        }
    }

    //Read each assembly granted internal access under <InternalsVisibleTo>
    private static void ReadFriends(XElement parent, string path, List<string> friends)
    {
        foreach (var element in parent.Elements())
        {
            //Both Name, the convention here, and Include, carried over from csproj, are accepted
            var name = (string?)element.Attribute("Name") ?? (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                Warn($"an element under <InternalsVisibleTo> in {path} has no Name, skipped");
                continue;
            }

            friends.Add(name);
        }
    }

    //Read each pattern packed into the resource bundle under <AvaloniaResources>
    private static void ReadResources(XElement parent, string path, List<string> resources)
    {
        foreach (var element in parent.Elements())
        {
            var include = (string?)element.Attribute("Include") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(include))
            {
                Warn($"an element under <AvaloniaResources> in {path} has no Include, skipped");
                continue;
            }

            resources.Add(include);
        }
    }

    //Read the development-time server settings under <Server>
    private static NcServer ReadServer(XElement element, string path)
        => new(
            (string?)element.Attribute("Cache") ?? string.Empty,
            (string?)element.Attribute("Args") ?? string.Empty,
            ReadBool(element, "Debug", false, path));

    //Read the development-time client settings under <Client>
    private static NcClient ReadClient(XElement element)
        => new(
            (string?)element.Attribute("Args") ?? string.Empty,
            (string?)element.Attribute("Version") ?? string.Empty,
            (string?)element.Attribute("Jar") ?? string.Empty);

    //Read the kernel source under <Sources>
    //Directory names a local kernel directory that replaces the cache, and Url is the download source, which also serves
    //as the fallback when that directory is not there; with neither one the built-in source is better and is kept
    private static NcSource? ReadSources(XElement element, string path)
    {
        var directory = (string?)element.Attribute("Directory") ?? string.Empty;
        var url = (string?)element.Attribute("Url") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(directory) && string.IsNullOrWhiteSpace(url))
        {
            Warn($"<Sources> in {path} has neither Directory nor Url, the built-in source is used");
            return null;
        }

        var format = (string?)element.Attribute("Format") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(format) && !NcSource.Formats.Contains(format, StringComparer.OrdinalIgnoreCase))
            Warn($"unknown Format=\"{format}\" in {path}, known ones are {string.Join(", ", NcSource.Formats)}");

        var pattern = (string?)element.Attribute("Pattern") ?? string.Empty;
        if (string.Equals(format, "regex", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(pattern))
            Warn($"Format=\"regex\" in {path} needs a Pattern, the built-in source is used");

        return new NcSource(directory, url, (string?)element.Attribute("Index") ?? string.Empty, format, pattern);
    }

    //Read the template source under <Template>, treating both fields being absent as unconfigured since the built-in source is better
    private static NcTemplate? ReadTemplate(XElement element, string path)
    {
        var url = (string?)element.Attribute("Url") ?? string.Empty;
        var baseUrl = (string?)element.Attribute("Base") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(baseUrl))
        {
            Warn($"<Template> in {path} has neither Url nor Base, the built-in source is used");
            return null;
        }

        return new NcTemplate(url, baseUrl);
    }

    //Read the check settings under <Check>
    private static NcCheck ReadCheck(XElement element)
        => new((string?)element.Attribute("LangVersion"));

    //Read the build settings under <Build>
    private static NcBuild ReadBuild(XElement element, string path)
    {
        var outputType = (string?)element.Attribute("OutputType") ?? string.Empty;
        if (outputType.Length > 0 && !NcBuild.OutputTypes.Contains(outputType, StringComparer.OrdinalIgnoreCase))
            Warn($"unknown OutputType=\"{outputType}\" in {path}, known ones are {string.Join(", ", NcBuild.OutputTypes)}");

        return new NcBuild(
            (string?)element.Attribute("AssemblyName") ?? string.Empty,
            (string?)element.Attribute("Configuration") ?? string.Empty,
            (string?)element.Attribute("Output") ?? string.Empty,
            (string?)element.Attribute("ExtraArgs") ?? string.Empty,
            ReadBool(element, "Nullable", true, path),
            ReadBool(element, "ImplicitUsings", true, path),
            (string?)element.Attribute("DefineConstants") ?? string.Empty,
            outputType,
            (string?)element.Attribute("RootNamespace") ?? string.Empty,
            ReadBool(element, "DependsOnModApi", NcBuild.DefaultDependsOnModApi, path),
            ReadBool(element, "AllowUnsafe", NcBuild.DefaultAllowUnsafe, path));
    }

    //Read each package dependency under <Packages>
    private static void ReadPackages(XElement parent, string path, List<NcPackage> packages)
    {
        foreach (var element in parent.Elements())
        {
            if (!Folded(element.Name.LocalName, "Package"))
            {
                Warn($"unknown element <{element.Name.LocalName}> under <Packages> in {path}, ignored");
                continue;
            }

            var id = (string?)element.Attribute("Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                Warn($"a <Package> in {path} has no Id, skipped");
                continue;
            }

            packages.Add(new NcPackage(id, (string?)element.Attribute("Version") ?? string.Empty));
        }
    }

    //Whether two element names differ only in case
    private static bool Folded(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    //Read each <Task> under <Tasks>
    private static void ReadTasks(XElement parent, bool globalOverride, string path, List<NcTask> tasks)
    {
        foreach (var element in parent.Elements())
        {
            if (!string.Equals(element.Name.LocalName, "Task", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"unknown element <{element.Name.LocalName}> under <Tasks> in {path}, ignored");
                continue;
            }

            var name = (string?)element.Attribute("Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Warn($"a <Task> in {path} has no Name, skipped");
                continue;
            }

            if (tasks.Any(task => string.Equals(task.Name, name, StringComparison.Ordinal)))
            {
                Warn($"task {name} is declared more than once in {path}, the later one is ignored");
                continue;
            }

            var description = (string?)element.Attribute("Description") ?? string.Empty;
            var depends = Split((string?)element.Attribute("Depends"));
            var overrides = ReadBool(element, "Override", globalOverride, path);
            tasks.Add(new NcTask(name, description, depends, overrides, ReadSteps(element, name, path)));
        }
    }

    //Read the steps of a task
    //Exec takes the element text, Copy and Zip take From and To, Tool takes Name, Method and Args
    //unsupported kinds only produce a warning
    private static List<NcStep> ReadSteps(XElement task, string name, string path)
    {
        var steps = new List<NcStep>();
        foreach (var step in task.Elements())
        {
            var kind = step.Name.LocalName;
            if (string.Equals(kind, "Exec", StringComparison.OrdinalIgnoreCase))
            {
                var text = step.Value.Trim();
                if (text.Length > 0)
                    steps.Add(NcStep.Exec(text));
                continue;
            }

            if (string.Equals(kind, "Tool", StringComparison.OrdinalIgnoreCase))
            {
                var plugin = (string?)step.Attribute("Name") ?? string.Empty;
                var method = (string?)step.Attribute("Method") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(method))
                {
                    Warn($"<Tool> in task {name} needs both Name and Method, skipped");
                    continue;
                }

                steps.Add(NcStep.Tool(plugin, method, (string?)step.Attribute("Args") ?? string.Empty));
                continue;
            }

            if (string.Equals(kind, "Script", StringComparison.OrdinalIgnoreCase))
            {
                var file = (string?)step.Attribute("File") ?? string.Empty;
                string source;
                if (file.Length > 0)
                {
                    var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                        System.IO.Path.GetDirectoryName(path)!, file));
                    if (!File.Exists(full))
                    {
                        Warn($"<Script> in task {name} points at {file}, which does not exist, skipped");
                        continue;
                    }

                    try
                    {
                        source = File.ReadAllText(full);
                    }
                    catch (IOException e)
                    {
                        Warn($"<Script> in task {name} cannot read {file}: {e.Message}, skipped");
                        continue;
                    }
                }
                else
                {
                    source = step.Value.Trim();
                    if (source.Length == 0)
                    {
                        Warn($"<Script> in task {name} has neither File nor inline source, skipped");
                        continue;
                    }
                }

                steps.Add(NcStep.Script(source, file));
                continue;
            }

            if (!string.Equals(kind, "Copy", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(kind, "Zip", StringComparison.OrdinalIgnoreCase))
            {
                Warn($"step <{kind}> in task {name} is not supported yet, ignored");
                continue;
            }

            var from = (string?)step.Attribute("From") ?? string.Empty;
            var to = (string?)step.Attribute("To") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                Warn($"<{kind}> in task {name} needs both From and To, skipped");
                continue;
            }

            steps.Add(string.Equals(kind, "Copy", StringComparison.OrdinalIgnoreCase)
                ? NcStep.Copy(from, to)
                : NcStep.Zip(from, to));
        }

        return steps;
    }

    //Split a separator-delimited attribute value, dropping whitespace and empty entries
    //Depends uses commas while path lists use semicolons
    private static List<string> Split(string? value, char separator = ',')
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    //Read a boolean attribute, warning and using the fallback when the value is unrecognized
    private static bool ReadBool(XElement element, string name, bool fallback, string path)
    {
        var raw = (string?)element.Attribute(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        if (bool.TryParse(raw, out var value))
            return value;

        Warn($"{name}=\"{raw}\" in {path} is not a boolean, using {fallback.ToString().ToLowerInvariant()}");
        return fallback;
    }

    //Tolerable config problems are reported here in yellow without aborting
    private static void Warn(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"warning: {message}");
        Console.ForegroundColor = previous;
    }
}

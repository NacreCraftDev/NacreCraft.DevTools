using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using NacreCraft.ModBuild.Tools;
//The two Diagnostic and two DiagnosticSeverity names clash, so the aliases below keep them apart
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using ProjectDiagnostic = NacreCraft.ModBuild.Diagnostics.Diagnostic;
using ProjectSeverity = NacreCraft.ModBuild.Diagnostics.DiagnosticSeverity;

namespace NacreCraft.ModBuild.Compile;

//Compiles the project straight into a mod assembly with Roslyn, producing the output and diagnostics without dotnet build or MSBuild projects
public static class Compiler
{
    //Path of the virtual assembly info tree, whose diagnostics are never reported
    private const string GeneratedPath = "AssemblyInfo.g.cs";

    //Compiles the mod assembly; assemblyName is the output name and target its full path
    //assembled may carry the compilation the check already built, which spares parsing and binding the same trees twice
    public static bool Compile(string root, NcProject project, string assemblyName, string target,
        ProjectCompilation? assembled, out string error)
    {
        error = string.Empty;

        //Check freshness first, since comparing timestamps is far cheaper than assembling a compilation
        if (Incremental.IsUpToDate(target, Inputs(root, project)))
        {
            Progress.WriteLine($"Up to date, {target}");
            return Deploy(root, project, target, out error);
        }

        var compiled = assembled;
        if (compiled is null)
        {
            compiled = CompilationFactory.Create(root, CompileOptions.From(project, assemblyName), out error);
            if (compiled is null)
                return false;
        }

        var compilation = AddAssemblyInfo(compiled, project, root, assemblyName);

        //Look at compile diagnostics first so a failing build never writes files
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error
                && diagnostic.Location.GetLineSpan().Path != GeneratedPath)
            .ToList();

        if (errors.Count > 0)
        {
            DiagnosticRenderer.Render(ToDiagnostics(compilation, errors, root));
            error = $"{errors.Count} compile error(s)";
            return false;
        }

        Progress.WriteLine($"Compiling {assemblyName} with Roslyn");
        if (!Emit(compilation, project, root, target, out error))
            return false;

        Progress.WriteLine($"Finished build, {target}");
        return Deploy(root, project, target, out error);
    }

    //Copies the referenced projects' outputs beside this assembly
    //A project reference is built for its types, but at run time the loader needs the dll itself, and the mods directory is
    //filled from this output folder, so the copy here is what carries a referenced mod into the server
    private static bool Deploy(string root, NcProject project, string target, out string error)
    {
        error = string.Empty;
        var directory = Path.GetDirectoryName(target)!;
        var copied = 0;

        foreach (var product in ProjectReferences.Products(root, project))
        {
            var source = Path.GetFullPath(product);
            var destination = Path.Combine(directory, Path.GetFileName(source));

            if (!File.Exists(source))
            {
                error = $"referenced project produced no assembly at {source}";
                return false;
            }

            //only a mod has to sit beside this assembly, since the mods directory is filled from this folder
            //anything else is embedded in the output instead, see Resources
            if (!ModAssembly.CarriesManifest(source))
                continue;

            //a reference may resolve to this very assembly when a project lists itself through a shared config
            if (string.Equals(source, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                File.Copy(source, destination, overwrite: true);
                copied++;
            }
            catch (IOException e)
            {
                error = $"cannot copy {source} to {destination}: {e.Message}";
                return false;
            }
        }

        if (copied > 0)
            Progress.WriteLine($"Copied {copied} referenced assembly(ies) to {directory}");

        return true;
    }

    //Every file that can affect this output; missing one risks serving a stale dll
    private static List<string> Inputs(string root, NcProject project)
    {
        var inputs = new List<string>(CompilationFactory.Inputs(root))
        {
            project.Path,
            Path.Combine(root, ModProject.ManifestName),
        };

        //The provisioned folders count too, since a new package, kernel or package targets should trigger a rebuild
        inputs.AddRange(Files(root, ProjectLayout.Packages));
        inputs.AddRange(Files(root, ProjectLayout.Kernel));
        inputs.AddRange(Files(root, ProjectLayout.Targets));

        //Avalonia resources live only inside this output, so nothing else would rebuild them when they change
        inputs.AddRange(AvaloniaResources.Inputs(root, project.AvaloniaResources));

        //Same for the embedded resources declared in the config, which appear only in this output
        inputs.AddRange(Embedded(root, project).Select(item => item.Path));

        //An embedded referenced assembly is part of this output too, so a change to it has to force a rebuild
        //The ones that ride beside the assembly are copied on every build instead, so they need no entry here
        inputs.AddRange(ProjectReferences.Products(root, project)
            .Where(path => File.Exists(path) && !ModAssembly.CarriesManifest(path)));

        var icon = ModProject.TryFind(root)?.IconPath;
        if (icon is not null)
            inputs.Add(icon);

        return inputs;
    }

    //All files under the directory, or empty when it is missing
    private static IEnumerable<string> Files(string root, string relative)
    {
        var directory = Path.Combine(root, relative);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            : [];
    }

    //Adds the assembly info tree the SDK used to generate, without which version and target framework metadata is incomplete
    private static Compilation AddAssemblyInfo(ProjectCompilation assembled, NcProject project, string root,
        string assemblyName)
    {
        var manifest = ModProject.TryFind(root);
        var version = manifest?.Version ?? string.Empty;
        var title = manifest?.DisplayName ?? assemblyName;

        var text = $"""
            // <auto-generated/>
            using System.Reflection;
            using System.Runtime.CompilerServices;
            using System.Runtime.Versioning;

            [assembly: AssemblyTitle("{Escape(title)}")]
            [assembly: AssemblyProduct("{Escape(title)}")]
            [assembly: AssemblyVersion("{AssemblyVersionOf(version)}")]
            [assembly: AssemblyFileVersion("{AssemblyVersionOf(version)}")]
            [assembly: AssemblyInformationalVersion("{Escape(version)}")]
            [assembly: TargetFramework("{TargetFramework()}", FrameworkDisplayName = ".NET")]
            {Friends(project)}
            """;

        var tree = CSharpSyntaxTree.ParseText(text, assembled.ParseOptions,
            path: GeneratedPath, encoding: Encoding.UTF8);
        return assembled.Compilation.AddSyntaxTrees(tree);
    }

    private static string Friends(NcProject project)
        => string.Join(Environment.NewLine, project.InternalsVisibleTo
            .Select(name => $"[assembly: InternalsVisibleTo(\"{Escape(name)}\")]"));

    //Writes the assembly and symbols through temp files that are only moved into place on success
    //Debug settings follow <Build>: a project can turn the pdb off, embed it, or ask for the xml documentation
    private static bool Emit(Compilation compilation, NcProject project, string root, string target, out string error)
    {
        error = string.Empty;
        var directory = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(directory, Path.GetFileName(target) + ".tmp");
        var symbols = Path.ChangeExtension(temporary, ".pdb");
        var documentation = Path.ChangeExtension(temporary, ".xml");

        //an embedded pdb travels inside the assembly, so no file is written next to it
        var format = FormatOf(project.Build.DebugType);
        var writesPdb = project.Build.DebugSymbols && format is not null
            && format is not DebugInformationFormat.Embedded;
        var writesDocumentation = project.Build.GenerateDocumentationFile;

        try
        {
            Directory.CreateDirectory(directory);
            using (var pe = File.Create(temporary))
            using (var pdb = writesPdb ? File.Create(symbols) : null)
            using (var xml = writesDocumentation ? File.Create(documentation) : null)
            {
                var result = compilation.Emit(
                    pe,
                    pdb,
                    xml,
                    win32Resources: null,
                    manifestResources: Resources(root, project),
                    options: new EmitOptions(
                        debugInformationFormat: format ?? DebugInformationFormat.PortablePdb));

                if (!result.Success)
                {
                    DiagnosticRenderer.Render(ToDiagnostics(compilation, result.Diagnostics, root));
                    error = "emit failed";
                    return false;
                }
            }

            File.Move(temporary, target, overwrite: true);
            if (writesPdb)
                File.Move(symbols, Path.ChangeExtension(target, ".pdb"), overwrite: true);

            if (writesDocumentation)
            {
                var destination = DocumentationOf(project, root, target);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(documentation, destination, overwrite: true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            error = $"cannot write {target}: {e.Message}";
            return false;
        }
        finally
        {
            Discard(temporary);
            Discard(symbols);
            Discard(documentation);
        }
    }

    //FormatOf maps the msbuild DebugType value, none meaning no symbols at all
    private static DebugInformationFormat? FormatOf(string value)
        => value.ToLowerInvariant() switch
        {
            "none" => null,
            "embedded" => DebugInformationFormat.Embedded,
            "full" => DebugInformationFormat.Pdb,
            _ => DebugInformationFormat.PortablePdb,
        };

    //DocumentationOf resolves where the xml documentation goes, the assembly name beside the output by default
    private static string DocumentationOf(NcProject project, string root, string target)
        => project.Build.DocumentationFile.Length > 0
            ? Path.GetFullPath(Path.Combine(root, project.Build.DocumentationFile))
            : Path.ChangeExtension(target, ".xml");

    //Resources to embed; the manifest is the loader's only way to recognize a mod and it points at the icon
    //Third party dependencies ride along because a distributed mod is one dll and the loader falls back to embedded resources for anything missing
    //Avalonia resources become their own package, the only form avares:// resolves
    private static List<ResourceDescription> Resources(string root, NcProject project)
    {
        var resources = new List<ResourceDescription>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        //Duplicate resource names fail emit outright, so the first one wins
        void Add(string name, Func<Stream> open)
        {
            if (seen.Add(name))
                resources.Add(new ResourceDescription(name, open, isPublic: true));
        }

        void AddFile(string name, string path) => Add(name, () => File.OpenRead(path));

        var manifest = Path.Combine(root, ModProject.ManifestName);
        if (File.Exists(manifest))
            AddFile(ModProject.ManifestName, manifest);

        var icon = ModProject.TryFind(root)?.IconPath;
        if (icon is not null && File.Exists(icon))
            AddFile(Path.GetFileName(icon), icon);

        var kernel = project.Build.SharedDependencies ? KernelNames(root) : null;
        foreach (var path in PackageResolver.Assemblies(root))
        {
            if (!File.Exists(path))
                continue;

            var name = Path.GetFileName(path);
            if (project.Build.NoEmbed.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Trace.Log($"package {name} is never embedded");
                continue;
            }

            if (kernel is not null && kernel.Contains(name))
            {
                Trace.Log($"package {name} is provided by the kernel, not embedded");
                continue;
            }

            AddFile(name, path);
        }

        if (project.AvaloniaResources.Count > 0)
        {
            var packed = AvaloniaResources.Pack(root, project.AvaloniaResources);
            if (packed is null)
                Progress.WriteLine("warning: <AvaloniaResources> matched no file, nothing is packed");
            else
                Add(AvaloniaResources.ResourceName, () => new MemoryStream(packed, writable: false));
        }

        //Ordinary resources from the config use LogicalName when given, otherwise the csproj naming rule applies
        foreach (var item in Embedded(root, project))
        {
            var name = item.LogicalName.Length > 0 ? item.LogicalName : DefaultName(root, project, item.Path);
            if (name is null)
            {
                Progress.WriteLine($"warning: {Path.GetRelativePath(root, item.Path)} is outside the project, "
                    + "add a LogicalName to its <Resource> in <EmbeddedResources>");
                continue;
            }

            AddFile(name, item.Path);
        }

        //A referenced project that is not a mod never reaches the mods directory, since staging only takes assemblies carrying
        //the manifest, so its assembly is embedded here instead and the loader resolves it as an embedded dependency
        foreach (var product in ProjectReferences.Products(root, project))
        {
            if (File.Exists(product) && !ModAssembly.CarriesManifest(product))
                AddFile(Path.GetFileName(product), product);
        }

        return resources;
    }

    //KernelNames the assemblies the framework already ships, which a mod does not have to carry again
    private static HashSet<string> KernelNames(string root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kernel = Path.Combine(root, ProjectLayout.Kernel);
        if (!Directory.Exists(kernel))
            return names;

        foreach (var path in Directory.EnumerateFiles(kernel, "*.dll", SearchOption.TopDirectoryOnly))
            names.Add(Path.GetFileName(path));
        return names;
    }

    //Expands the resources declared in <EmbeddedResources> and warns when a pattern matches nothing
    private static List<ExpandedResource> Embedded(string root, NcProject project)
    {
        var found = new List<ExpandedResource>();
        foreach (var resource in project.EmbeddedResources)
        {
            var matched = PathPattern.Match(root, resource.Include).ToList();
            if (matched.Count == 0)
            {
                Progress.WriteLine($"warning: <EmbeddedResources> matched no file for {resource.Include}");
                continue;
            }

            foreach (var path in matched)
                found.Add(new ExpandedResource(path, resource.LogicalName));
        }

        return found;
    }

    //A matched resource whose name may still be empty for the caller to derive
    private sealed record ExpandedResource(string Path, string LogicalName);

    //Derives the resource name from root namespace plus relative path, which is impossible for files outside the project
    private static string? DefaultName(string root, NcProject project, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return null;

        var prefix = project.Build.RootNamespace.Length > 0
            ? project.Build.RootNamespace
            : project.Build.AssemblyName.Length > 0
                ? project.Build.AssemblyName
                : new DirectoryInfo(root).Name;

        var name = relative.Replace('\\', '.').Replace('/', '.');
        return prefix.Length > 0 ? prefix + "." + name : name;
    }

    //Converts Roslyn diagnostics into the project's own type so the output matches the check path
    private static List<ProjectDiagnostic> ToDiagnostics(Compilation compilation,
        IEnumerable<RoslynDiagnostic> diagnostics, string root)
    {
        var items = new List<ProjectDiagnostic>();
        //candidates for a missing using are memoized across the errors of this compilation
        var namespaces = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var diagnostic in diagnostics)
        {
            var span = diagnostic.Location.GetLineSpan();
            var file = string.IsNullOrEmpty(span.Path)
                ? string.Empty
                : Path.GetRelativePath(root, span.Path).Replace('\\', '/');

            var fixes = CSharpAnalyzer.Fixes(compilation, diagnostic, namespaces, root);

            items.Add(new ProjectDiagnostic(
                ProjectSeverity.Error,
                diagnostic.Id,
                CSharpAnalyzer.MessageOf(diagnostic, fixes),
                file,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                Math.Max(1, diagnostic.Location.SourceSpan.Length),
                SourceLine(span.Path, span.StartLinePosition.Line + 1),
                string.Empty,
                diagnostic.Descriptor.HelpLinkUri,
                fixes));
        }
        return items;
    }

    //Reads the source line, returning empty when unavailable
    private static string SourceLine(string path, int line)
    {
        if (string.IsNullOrEmpty(path) || path == GeneratedPath)
            return string.Empty;

        var text = SourceTextCache.Read(path);
        if (text is null)
            return string.Empty;

        var index = line - 1;
        if (index < 0)
            return string.Empty;

        //only the requested line is walked, an error list can name the same file hundreds of times
        var start = 0;
        for (var current = 0; current < index; current++)
        {
            start = text.IndexOf('\n', start);
            if (start < 0)
                return string.Empty;
            start++;
        }

        var end = text.IndexOf('\n', start);
        var lineText = end < 0 ? text[start..] : text[start..end];
        return lineText.TrimEnd('\r');
    }

    //Pads to four numeric segments, so 1.2.3 becomes 1.2.3.0 and suffixes are dropped
    private static string AssemblyVersionOf(string version)
    {
        var parts = new List<string>();
        foreach (var part in version.Split('.', '-', '+'))
        {
            if (!int.TryParse(part, out _))
                break;

            parts.Add(part);
            if (parts.Count == 4)
                break;
        }

        while (parts.Count < 4)
            parts.Add("0");
        return string.Join('.', parts);
    }

    private static string TargetFramework()
    {
        const string marker = "Version=v";
        var name = AppContext.TargetFrameworkName ?? string.Empty;
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "net10.0" : "net" + name[(index + marker.Length)..];
    }

    //Escapes quotes and backslashes because attribute arguments sit inside a string literal
    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            //A failed delete is harmless since the next run overwrites the file
        }
    }
}

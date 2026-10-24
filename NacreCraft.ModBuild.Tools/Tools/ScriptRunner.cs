using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Tools;

//ScriptRunner runs one <Script> step, the experimental javascript entry point
//The engine gets a handful of shared ncm objects; raw CLR access stays off unless the project asks for it
internal static class ScriptRunner
{
    public static bool Run(NcStep step, NcProject project, IReadOnlyDictionary<string, string> variables,
        string root, bool allowClr)
    {
        var engine = ScriptApi.Create(project, variables, root, allowClr);

        var name = step.File.Length > 0 ? step.File : "ncproj:script";
        try
        {
            engine.Execute(step.Source, name);
            return true;
        }
        catch (JavaScriptException e)
        {
            Fail($"{name}:{e.Location.Start.Line}:{e.Location.Start.Column} {FirstLine(e.Message)}");
            return false;
        }
        catch (Exception e)
        {
            Fail($"{name} stopped: {e.GetType().Name}: {FirstLine(e.Message)}");
            return false;
        }
    }

    private static void Fail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {message}");
        Console.ResetColor();
    }

    private static string FirstLine(string text)
        => text.Split('\n')[0].TrimEnd('\r');
}

//ScriptLog writes to the task output
public sealed class ScriptLog
{
    public void info(string message) => Console.WriteLine(message);

    public void warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    public void error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}

//ScriptProject is the read side of the ncproj and the mod manifest
public sealed class ScriptProject
{
    private readonly IReadOnlyDictionary<string, string> _variables;

    public ScriptProject(NcProject project, IReadOnlyDictionary<string, string> variables, string root)
    {
        _variables = variables;
        dir = root;
        path = project.Path;
        configuration = variables.TryGetValue("Configuration", out var value) ? value : string.Empty;
        build = new ScriptBuild(project.Build, project.Check.LangVersion);
        tasks = project.Tasks.Select(task => new ScriptTaskView(task)).ToArray();
        packages = project.Packages.Select(item => new ScriptPackageView(item)).ToArray();
        sources = SourceFiles.Enumerate(root)
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        var manifest = ModProject.TryFind(root);
        if (manifest is not null)
        {
            name = manifest.DisplayName;
            modId = manifest.Id;
            modVersion = manifest.Version;
            this.manifest = new ScriptManifest(manifest);
        }
        else
        {
            name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    public string dir { get; }

    public string path { get; }

    public string name { get; }

    public string modId { get; } = string.Empty;

    public string modVersion { get; } = string.Empty;

    public string configuration { get; }

    public ScriptBuild build { get; }

    public ScriptTaskView[] tasks { get; }

    public ScriptPackageView[] packages { get; }

    public string[] sources { get; }

    public ScriptManifest? manifest { get; }
}

//ScriptBuild mirrors the build settings a task may want to branch on
public sealed class ScriptBuild
{
    public ScriptBuild(NcBuild build, string langVersion)
    {
        assemblyName = build.AssemblyName;
        output = build.Output;
        outputType = build.OutputType;
        nullable = build.Nullable;
        allowUnsafe = build.AllowUnsafe;
        this.langVersion = langVersion;
    }

    public string assemblyName { get; }

    public string output { get; }

    public string outputType { get; }

    public bool nullable { get; }

    public bool allowUnsafe { get; }

    public string langVersion { get; }
}

//ScriptTaskView one task as data, used to build task graphs in a script
public sealed class ScriptTaskView
{
    public ScriptTaskView(NcTask task)
    {
        name = task.Name;
        description = task.Description;
        depends = task.Depends.ToArray();
        overrides = task.Overrides;
        steps = task.Steps.Select(step => step.Text).ToArray();
    }

    public string name { get; }

    public string description { get; }

    public string[] depends { get; }

    public bool overrides { get; }

    public string[] steps { get; }
}

//ScriptPackageView one package reference
public sealed class ScriptPackageView
{
    public ScriptPackageView(NcPackage package)
    {
        id = package.Id;
        version = package.Version;
    }

    public string id { get; }

    public string version { get; }
}

//ScriptManifest the values of ncmod.json
public sealed class ScriptManifest
{
    public ScriptManifest(ModProject manifest)
    {
        id = manifest.Id;
        name = manifest.DisplayName;
        version = manifest.Version;
        ns = manifest.Namespace;
        icon = manifest.IconPath;
        path = manifest.ManifestPath;
    }

    public string id { get; }

    public string name { get; }

    public string version { get; }

    public string ns { get; }

    public string icon { get; }

    public string path { get; }
}

//ScriptFs reads and writes inside the project, a path outside it is refused
public sealed class ScriptFs
{
    private readonly string _root;

    public ScriptFs(string root) => _root = Path.GetFullPath(root);

    public string read(string path) => File.ReadAllText(Locate(path));

    public void write(string path, string text)
    {
        var full = Locate(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public bool exists(string path)
    {
        var full = Locate(path);
        return File.Exists(full) || Directory.Exists(full);
    }

    public string[] glob(string pattern)
        => PathPattern.Match(_root, pattern)
            .Select(file => Path.GetRelativePath(_root, file))
            .ToArray();

    public string[] list(string pattern) => glob(pattern);

    public void mkdir(string path) => Directory.CreateDirectory(Locate(path));

    public void remove(string path)
    {
        var full = Locate(path);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
        else if (File.Exists(full))
            File.Delete(full);
    }

    public void copy(string from, string to)
    {
        var source = Locate(from);
        var target = Locate(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    public string Locate(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path));
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{path} is outside the project");

        return full;
    }
}

//ScriptPath is the System.IO.Path part a script usually needs
public sealed class ScriptPath
{
    private readonly string _root;

    public ScriptPath(string root) => _root = Path.GetFullPath(root);

    public string join(string left, string right) => Path.Combine(left, right);

    public string join(string left, string middle, string right) => Path.Combine(left, middle, right);

    public string dirname(string path) => Path.GetDirectoryName(path) ?? string.Empty;

    public string basename(string path) => Path.GetFileName(path);

    public string extname(string path) => Path.GetExtension(path);

    public string absolute(string path) => Path.GetFullPath(Path.Combine(_root, path));

    public string relative(string from, string to)
        => Path.GetRelativePath(
            Path.GetFullPath(Path.Combine(_root, from)),
            Path.GetFullPath(Path.Combine(_root, to)));
}

//ScriptEnv reads environment variables without CLR access
public sealed class ScriptEnv
{
    public string get(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    public string[] names() => Environment.GetEnvironmentVariables().Keys
        .Cast<string>()
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

//ScriptHash hashes text or a project file
public sealed class ScriptHash
{
    private readonly ScriptFs _fs;

    public ScriptHash(ScriptFs fs) => _fs = fs;

    public string sha256(string text)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public string sha256File(string path)
        => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_fs.Locate(path))));
}

//ScriptXml queries an xml document with xpath
public sealed class ScriptXml
{
    public ScriptXmlNode[] select(string text, string xpath)
        => XDocument.Parse(text)
            .XPathSelectElements(xpath)
            .Select(node => new ScriptXmlNode(node))
            .ToArray();

    public ScriptXmlNode root(string text) => new(XDocument.Parse(text).Root!);
}

//ScriptXmlNode one selected element
public sealed class ScriptXmlNode
{
    private readonly Dictionary<string, string> _attributes;

    public ScriptXmlNode(XElement element)
    {
        name = element.Name.LocalName;
        value = element.Value;
        _attributes = element.Attributes()
            .ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value);
    }

    public string name { get; }

    public string value { get; }

    public Dictionary<string, string> attributes => _attributes;

    public string attribute(string name)
        => _attributes.TryGetValue(name, out var value) ? value : string.Empty;
}

//ScriptSyntax gives the script a flat view of the project syntax trees
//Roslyn nodes stay on this side, so the api can stay small and positional data survives
public sealed class ScriptSyntax
{
    private readonly string _root;

    private readonly Engine _engine;

    private List<SyntaxNodeView>? _nodes;

    public ScriptSyntax(string root, Engine engine)
    {
        _root = Path.GetFullPath(root);
        _engine = engine;
    }

    public object[] query(string kind)
        => Nodes()
            .Where(node => string.Equals(node.kind, kind, StringComparison.Ordinal))
            .ToArray();

    public int walk(string kind, JsValue visit)
    {
        var count = 0;
        foreach (var node in Nodes())
        {
            if (!string.Equals(node.kind, kind, StringComparison.Ordinal))
                continue;

            visit.Call(JsValue.FromObject(_engine, node));
            count++;
        }

        return count;
    }

    private List<SyntaxNodeView> Nodes()
    {
        if (_nodes is not null)
            return _nodes;

        var nodes = new List<SyntaxNodeView>();
        foreach (var file in SourceFiles.Enumerate(_root))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var span = node.GetLocation().GetLineSpan();
                nodes.Add(new SyntaxNodeView
                {
                    kind = node.Kind().ToString(),
                    file = Path.GetRelativePath(_root, file),
                    line = span.StartLinePosition.Line + 1,
                    column = span.StartLinePosition.Character + 1,
                    name = NameOf(node),
                    text = Snippet(node.ToString()),
                });
            }
        }

        _nodes = nodes;
        return nodes;
    }

    private static string NameOf(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax declared => declared.Identifier.ValueText,
        MethodDeclarationSyntax declared => declared.Identifier.ValueText,
        PropertyDeclarationSyntax declared => declared.Identifier.ValueText,
        VariableDeclaratorSyntax declared => declared.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string Snippet(string text)
        => text.Length <= 160 ? text : text[..157] + "...";
}

//SyntaxNodeView one syntax node as the script sees it
public sealed class SyntaxNodeView
{
    public string kind { get; init; } = string.Empty;

    public string file { get; init; } = string.Empty;

    public int line { get; init; }

    public int column { get; init; }

    public string name { get; init; } = string.Empty;

    public string text { get; init; } = string.Empty;
}

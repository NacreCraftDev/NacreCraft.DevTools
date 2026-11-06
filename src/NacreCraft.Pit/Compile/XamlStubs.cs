using System.Text;
using System.Xml.Linq;
using NacreCraft.Pit.Core;
using NacreCraft.Pit.Diagnostics;

namespace NacreCraft.Pit.Compile;

//Declares the fields an Avalonia UI file names with x:Name
//The UI compiler that normally emits them is an msbuild task, which a plain check never runs, so a code-behind file
//reads its own controls as missing types; a partial declaration with the same fields lets the sources bind as they
//do in a real build. Only used for a plain csproj, since a mod project goes through the package targets instead
internal static class XamlStubs
{
    //Path the stub tree is parsed under, which the check skips when it reports diagnostics
    public const string Path = "pit.xaml.g.cs";

    private const string AvaloniaUri = "https://github.com/avaloniaui";
    private const string XamlUri = "http://schemas.microsoft.com/winfx/2006/xaml";

    //Sources one stub tree per UI file, empty when the file names no control
    public static IReadOnlyList<string> Sources(string root)
    {
        var sources = new List<string>();
        foreach (var file in SourceFiles.Enumerate(root, "*.axaml"))
        {
            var source = Stub(file);
            if (source is not null)
                sources.Add(source);
        }

        return sources;
    }

    private static string? Stub(string file)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(file);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            Trace.Log($"cannot read {file} for its named controls: {e.Message}");
            return null;
        }

        var root = document.Root;
        if (root is null)
            return null;

        //x:Class names the class the fields belong to, a styles file has none
        var owner = root.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "Class" && attribute.Name.NamespaceName == XamlUri)?
            .Value;
        if (string.IsNullOrWhiteSpace(owner))
            return null;

        var split = owner.LastIndexOf('.');
        var space = split > 0 ? owner[..split] : string.Empty;
        var name = split > 0 ? owner[(split + 1)..] : owner;

        var fields = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in root.DescendantsAndSelf())
        {
            var field = element.Attributes()
                .FirstOrDefault(attribute => attribute.Name.LocalName == "Name" && attribute.Name.NamespaceName == XamlUri)?
                .Value;
            if (string.IsNullOrWhiteSpace(field) || !IsIdentifier(field) || !seen.Add(field))
                continue;

            var type = TypeOf(element);
            if (type is null)
                continue;

            fields.AppendLine($"    internal global::{type} {field} = null!;");
        }

        if (fields.Length == 0)
            return null;

        var builder = new StringBuilder();
        builder.AppendLine("//Fields for the named controls of a UI file, standing in for the ones its msbuild task would generate");
        builder.AppendLine("#nullable enable");
        if (space.Length > 0)
        {
            builder.AppendLine($"namespace {space};");
            builder.AppendLine();
        }

        builder.AppendLine($"partial class {name}");
        builder.AppendLine("{");
        builder.Append(fields);
        builder.AppendLine("}");
        return builder.ToString();
    }

    //TypeOf resolves an element to the clr type it builds, null when its namespace is not one this can spell
    private static string? TypeOf(XElement element)
    {
        var uri = element.Name.NamespaceName;
        string space;
        if (uri == AvaloniaUri)
            space = "Avalonia.Controls";
        else if (uri.StartsWith("using:", StringComparison.Ordinal))
            space = uri["using:".Length..].Trim();
        else if (uri.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var body = uri["clr-namespace:".Length..];
            var end = body.IndexOf(';');
            space = (end < 0 ? body : body[..end]).Trim();
        }
        else
            return null;

        var local = element.Name.LocalName;
        return space.Length == 0 ? local : $"{space}.{local}";
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || !(char.IsLetter(value[0]) || value[0] == '_'))
            return false;

        return value.All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}

using System.Runtime.Serialization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Compile;

//Packs Avalonia resources into the single package the loader understands
//Layout matches Avalonia.Build.Tasks' GenerateAvaloniaResourcesTask: a four byte index length, the index, then the file data
//Entries record path, offset and size, which StandardAssetLoader uses to resolve avares:// addresses at runtime
//The x:Class to resource path mapping travels as a DataContract serialized entry
public static class AvaloniaResources
{
    //The one resource name the loader looks for in the assembly
    public const string ResourceName = "!AvaloniaResources";

    //Special entry holding the x:Class to resource path mapping
    private const string XamlInfoPath = "/!AvaloniaResourceXamlInfo";

    private const int IndexVersion = 2;

    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    //Mirrors Avalonia's contract namespace so both sides read the same xml
    private const string XamlInfoNamespace = "http://schemas.datacontract.org/2004/07/Avalonia.Markup.Xaml.PortableXaml";

    private const string XamlInfoRoot = "AvaloniaResourceXamlInfo";

    //Files that go into the package, which the incremental check watches
    public static IReadOnlyList<string> Inputs(string root, IReadOnlyList<string> patterns)
        => Collect(root, patterns);

    //Packs the matched resources, returning null when nothing matches; patterns are project root relative and support * ? **
    public static byte[]? Pack(string root, IReadOnlyList<string> patterns)
    {
        var files = Collect(root, patterns);
        if (files.Count == 0)
            return null;

        var entries = new List<Entry>();
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in files)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            entries.Add(new Entry("/" + relative, new FileInfo(path).Length, () => File.OpenRead(path)));

            //The runtime locates axaml by class name through this mapping
            if (relative.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                && ClassOf(path) is { Length: > 0 } name)
            {
                classes[name] = "/" + relative;
            }
        }

        var info = XamlInfo(classes);
        entries.Add(new Entry(XamlInfoPath, info.Length, () => new MemoryStream(info, writable: false)));

        return Write(entries);
    }

    //Adds every axaml in the project to the configured patterns, sorted so the same input always yields the same package
    private static List<string> Collect(string root, IReadOnlyList<string> patterns)
    {
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            foreach (var path in PathPattern.Match(root, pattern))
                files.Add(path);
        }

        //axaml is an Avalonia default item, so listing it in the config is unnecessary
        foreach (var path in SourceFiles.Enumerate(root, "*.axaml"))
            files.Add(path);

        return files.ToList();
    }

    //Writes the index before the data because the leading index length is only known once the index is built
    private static byte[] Write(List<Entry> entries)
    {
        var index = new MemoryStream();
        using (var writer = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(IndexVersion);
            writer.Write(entries.Count);

            var offset = 0;
            foreach (var entry in entries)
            {
                writer.Write(entry.Path);
                writer.Write(offset);
                writer.Write((int)entry.Size);
                offset += (int)entry.Size;
            }
        }

        var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((int)index.Length);
            index.Position = 0;
            index.CopyTo(output);

            foreach (var entry in entries)
            {
                using var stream = entry.Open();
                stream.CopyTo(output);
            }
        }

        return output.ToArray();
    }

    //Root and namespace are spelled out to match Avalonia's copy so both sides read the same xml
    private static byte[] XamlInfo(Dictionary<string, string> classes)
    {
        var serializer = new DataContractSerializer(typeof(XamlInfoContract), XamlInfoRoot, XamlInfoNamespace);
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, new XamlInfoContract { ClassToResourcePathIndex = classes });
        return stream.ToArray();
    }

    //Reads x:Class from the axaml root, returning null when it is absent or unreadable
    private static string? ClassOf(string path)
    {
        try
        {
            return XDocument.Load(path).Root?.Attribute(XName.Get("Class", XamlNamespace))?.Value;
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            Trace.Log($"cannot read {path}: {e.Message}");
            return null;
        }
    }

    private sealed record Entry(string Path, long Size, Func<Stream> Open);

    //Namespace must match Avalonia's type or the members deserialize into the wrong place
    [DataContract(Namespace = XamlInfoNamespace)]
    private sealed class XamlInfoContract
    {
        [DataMember]
        public Dictionary<string, string> ClassToResourcePathIndex { get; set; } = new();
    }
}

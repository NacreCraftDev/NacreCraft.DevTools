using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NacreCraft.ModBuild.Tools;

//One type row the assembly page lists
public sealed record AsmTypeRow(string Kind, string Name);

//One resource row the assembly page lists; a linked resource has no size of its own
public sealed record AsmResourceRow(string Name, long Size, string Kind);

//What the page shows after opening an assembly
public sealed record AsmSummary(string Name, string Version, int Methods, IReadOnlyList<AsmTypeRow> Types);

//AsmBridge gives the language server the same operations as the asm command, without a console or another process
public static class AsmBridge
{
    //Summary the assembly name, version, method count and every type
    public static AsmSummary Summary(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        var types = AsmTool.Types(reader)
            .Select(row => new AsmTypeRow(row.Kind, row.FullName))
            .ToList();

        return new AsmSummary(
            reader.GetString(assembly.Name),
            assembly.Version?.ToString() ?? "0.0.0.0",
            AsmTool.MethodCount(reader),
            types);
    }

    //Resources the embedded and linked resources, with the loader's reading of each
    public static IReadOnlyList<AsmResourceRow> Resources(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var rows = new List<AsmResourceRow>();
        foreach (var handle in reader.ManifestResources)
        {
            var resource = reader.GetManifestResource(handle);
            var embedded = resource.Implementation.IsNil;
            var name = reader.GetString(resource.Name);
            rows.Add(new AsmResourceRow(
                name,
                embedded ? AsmTool.EmbeddedSize(pe, resource) : -1,
                AsmTool.KindOf(name, embedded)));
        }

        return rows;
    }

    //Details the fields, properties and methods of one type
    public static string Details(string path, string query) => AsmTool.Details(path, query);

    //Decompile turns one type into C# source, resolving what it can from the NuGet cache
    public static string Decompile(string path, string query, IReadOnlyList<string>? references = null)
        => AsmTool.Decompile(path, query, references?.ToList() ?? []);

    //Il disassembles the method bodies of one type
    public static string Il(string path, string query) => AsmTool.Il(path, query);

    //Dependencies the assembly reference list
    public static string Dependencies(string path) => AsmTool.Dependencies(path);

    //ReadResource the bytes of one embedded resource
    public static byte[] ReadResource(string path, string name) => AsmTool.ReadResource(path, name);

    //Embed writes files into the target and returns what changed
    public static IReadOnlyList<string> Embed(string target, IReadOnlyList<string> files)
        => AsmTool.Embed(target, files);

    //Remove drops resources from the target and returns what changed
    public static IReadOnlyList<string> Remove(string target, IReadOnlyList<string> names)
        => AsmTool.Drop(target, names);
}

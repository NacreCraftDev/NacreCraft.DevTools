using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Compile;

//ModAssembly tells a mod apart from a plain library by the manifest the loader itself goes by
//Only metadata is read, no assembly is loaded
public static class ModAssembly
{
    //CarriesManifest whether the assembly embeds ncmod.json, the one thing the loader recognizes a mod by
    public static bool CarriesManifest(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return false;

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.ManifestResources)
            {
                var resource = reader.GetManifestResource(handle);

                //an embedded manifest and a linked one share the name, only the embedded one counts
                if (resource.Implementation.IsNil && reader.GetString(resource.Name) == ModProject.ManifestName)
                    return true;
            }

            return false;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            Trace.Log($"cannot inspect {path}: {e.GetType().Name}");
            return false;
        }
    }
}

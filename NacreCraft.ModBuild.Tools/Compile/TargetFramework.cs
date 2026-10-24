using NuGet.Frameworks;

namespace NacreCraft.ModBuild.Compile;

//Target framework of the current process, read from ncm's own runtime because a mod must share the kernel's runtime
internal static class TargetFramework
{
    //Short name such as net10.0
    public static string Name { get; } = Resolve();

    //NuGet's representation, needed to pick framework directories inside packages
    public static NuGetFramework NuGet { get; } = NuGetFramework.Parse(Name);

    //Parses the runtime's framework name such as .NETCoreApp,Version=v10.0
    private static string Resolve()
    {
        const string marker = "Version=v";
        var name = AppContext.TargetFrameworkName ?? string.Empty;
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "net10.0" : "net" + name[(index + marker.Length)..];
    }
}

namespace NacreCraft.ModBuild.Core;

//The project's fixed directories; kernel references, restored packages and output each take a layer under Build so a rename happens only here
internal static class ProjectLayout
{
    //Root build directory name
    public const string Build = "Build";

    //Name of the folder holding kernel assemblies, both under Build and in a server build output
    public const string KernelFolder = "kernel";

    //Where referenced kernel assemblies land; the batch synced from templates doubles as compile-time references
    public static readonly string Kernel = Path.Combine(Build, KernelFolder);

    //Where restored NuGet packages land, one subdirectory per package id
    public static readonly string Packages = Path.Combine(Build, "packages");

    //Where packages' props and targets land, one slot per package
    public static readonly string Targets = Path.Combine(Build, "targets");

    //Intermediate directory handed to the target for execution
    public static readonly string Intermediate = Path.Combine(Build, "obj");

    public static readonly string Output = Path.Combine(Build, "out");
}

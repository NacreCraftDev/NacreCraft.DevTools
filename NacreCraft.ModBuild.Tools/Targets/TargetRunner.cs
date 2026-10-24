using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Tools;

//Composes a project in memory to run the targets shipped by the packages through the msbuild engine
//The composed project never hits disk and does not compile anything, it only borrows engine evaluation and local task running
//Compilation itself stays with the Roslyn direct build, so this is only the post-compile step
internal static class TargetRunner
{
    //Targets to run after compilation, skipped when the project does not define them
    //Names match the package targets, add another package's target name here to wire it in
    private static readonly string[] PostBuildTargets = ["CompileAvaloniaXaml"];

    //Runs the post-build targets and reports whether the build can continue
    //The assembly is the direct build's output and the targets usually rewrite it in place
    public static bool Run(string root, NcProject project, string assembly, out string error)
    {
        error = string.Empty;

        //The engine follows the local sdk, so a machine without one skips this layer instead of failing the whole build
        if (!MsBuildLibrary.Attach(out var engineError))
        {
            Console.WriteLine($"warning: {engineError}, the targets of the packages are skipped");
            return true;
        }

        try
        {
            Sdks();
            var xml = Compose(root, project, assembly);
            Trace.Log(xml);

            using var reader = XmlReader.Create(new StringReader(xml));
            var element = ProjectRootElement.Create(reader);
            element.FullPath = Path.Combine(root, "ncm.targets.proj");

            var collection = new ProjectCollection();
            var evaluated = new Project(element, globalProperties: null, toolsVersion: null, projectCollection: collection);
            var logger = new ConsoleLogger(LoggerVerbosity.Normal);

            var ran = 0;
            foreach (var target in PostBuildTargets)
            {
                if (!evaluated.Targets.Any(item => item.Key == target))
                    continue;

                Console.WriteLine($"Running target {target}");
                if (!evaluated.Build(target, [logger]))
                {
                    error = $"target {target} failed";
                    return false;
                }

                ran++;
            }

            Trace.Log($"ran {ran} post build target(s)");
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"target runner failure {e}");
            return false;
        }
    }

    //Composes the in-memory project
    //Properties and items mirror the direct build so references come from the precomputed list instead of package restore
    private static string Compose(string root, NcProject project, string assembly)
    {
        var name = AssemblyName(root, project);
        var builder = new StringBuilder();
        builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");

        builder.AppendLine("  <PropertyGroup>");
        builder.AppendLine($"    <TargetFramework>{TargetFramework.Name}</TargetFramework>");
        builder.AppendLine($"    <Configuration>{project.Build.Configuration}</Configuration>");
        builder.AppendLine($"    <AssemblyName>{Escape(name)}</AssemblyName>");
        builder.AppendLine($"    <RootNamespace>{Escape(RootNamespace(project, name))}</RootNamespace>");
        builder.AppendLine("    <EnableDefaultItems>false</EnableDefaultItems>");
        builder.AppendLine("    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>");
        builder.AppendLine("    <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>");
        builder.AppendLine("    <DesignTimeBuild>false</DesignTimeBuild>");
        //References and packages are already resolved by the direct build, so nuget assets and the assets file do not apply here
        builder.AppendLine("    <SkipResolvePackageAssets>true</SkipResolvePackageAssets>");
        //This project produces no reference assembly, so the refint path the sdk hands out by default is a file nothing creates
        //Only the sdk's own compile target would create it and the direct build never runs that target
        //Avalonia timestamps the missing file when it decides no write-back is needed, which throws
        builder.AppendLine("    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>");
        //The output is staged at the sdk's own intermediate location, which is what the targets rewrite before it is collected back into the output directory
        builder.AppendLine($"    <NcmAssembly>{Escape(assembly)}</NcmAssembly>");
        builder.AppendLine($"    <NcmOutput>{Escape(assembly)}</NcmOutput>");
        builder.AppendLine($"    <BaseIntermediateOutputPath>{Escape(Intermediate(root))}{Path.DirectorySeparatorChar}</BaseIntermediateOutputPath>");
        builder.AppendLine($"    <IntermediateOutputPath>{Escape(Intermediate(root))}{Path.DirectorySeparatorChar}</IntermediateOutputPath>");
        builder.AppendLine($"    <MSBuildProjectExtensionsPath>{Escape(Intermediate(root))}{Path.DirectorySeparatorChar}</MSBuildProjectExtensionsPath>");
        builder.AppendLine($"    <OutputPath>{Escape(Path.Combine(root, ProjectLayout.Output))}{Path.DirectorySeparatorChar}</OutputPath>");
        builder.AppendLine("  </PropertyGroup>");

        builder.AppendLine("  <ItemGroup>");
        foreach (var path in CompilationFactory.References(root))
            builder.AppendLine($"    <Reference Include=\"{Escape(path)}\" />");

        foreach (var file in SourceFiles.Enumerate(root, "*.axaml"))
            builder.AppendLine($"    <AvaloniaXaml Include=\"{Escape(file)}\" />");
        builder.AppendLine("  </ItemGroup>");

        //Package targets are imported with their own relative layout so task assembly paths resolve through MSBuildThisFileDirectory
        foreach (var import in PackageTargets.Imports(root))
            builder.AppendLine($"  <Import Project=\"{Escape(import)}\" />");

        //Stage the assembly wherever the sdk expects intermediates instead of guessing its directory layout
        builder.AppendLine("""
              <Target Name="NcmStageAssembly" BeforeTargets="CompileAvaloniaXaml">
                <Copy SourceFiles="$(NcmAssembly)" DestinationFiles="@(IntermediateAssembly)" SkipUnchangedFiles="false" />
              </Target>
              <Target Name="NcmCollectAssembly" AfterTargets="CompileAvaloniaXaml" Condition="Exists('$(NcmOutput)')">
                <Copy SourceFiles="@(IntermediateAssembly)" DestinationFiles="$(NcmOutput)" SkipUnchangedFiles="false" />
              </Target>
            """);

        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    //Intermediate directory handed to the targets, kept under Build
    private static string Intermediate(string root)
        => Path.Combine(root, ProjectLayout.Intermediate);

    //Output name, derived the same way as the direct build
    private static string AssemblyName(string root, NcProject project)
        => string.IsNullOrWhiteSpace(project.Build.AssemblyName)
            ? new DirectoryInfo(root).Name
            : project.Build.AssemblyName;

    //Uses the configured root namespace, falling back to the assembly name
    private static string RootNamespace(NcProject project, string name)
        => string.IsNullOrWhiteSpace(project.Build.RootNamespace) ? name : project.Build.RootNamespace;

    //Points the engine at the local sdk, without which the Sdk attribute cannot be evaluated
    //The extensions paths must point at the sdk root too so Current\Microsoft.Common.props resolves
    internal static void Sdks()
    {
        var sdk = MsBuildLibrary.SdkDirectory;
        if (sdk is null)
            return;

        var sdkRoot = sdk + Path.DirectorySeparatorChar;
        Keep("MSBuildSDKsPath", Path.Combine(sdk, "Sdks"));
        Keep("MSBuildExtensionsPath", sdkRoot);
        Keep("MSBuildExtensionsPath32", sdkRoot);
        Keep("MSBuildExtensionsPath64", sdkRoot);
        //The engine locates SdkResolvers relative to the msbuild assembly, so pointing at the sdk copy makes it accept the sdk directory
        var exe = Path.Combine(sdk, "MSBuild.dll");
        if (File.Exists(exe))
            Keep("MSBUILD_EXE_PATH", exe);

        Trace.Log($"sdk {sdk}");
    }

    //Only fills unset variables, leaving already-pointed ones untouched
    private static void Keep(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

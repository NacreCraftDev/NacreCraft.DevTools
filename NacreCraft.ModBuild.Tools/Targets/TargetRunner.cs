using System.Text;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
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
            Progress.WriteLine($"warning: {engineError}, the targets of the packages are skipped");
            return true;
        }

        try
        {
            Sdks();
            var xml = Compose(root, project, assembly);
            //Composing the project is a visible step of its own, the engine work follows it
            Progress.Step("compose project");
            Trace.Log(xml);

            using var reader = XmlReader.Create(new StringReader(xml));
            var element = ProjectRootElement.Create(reader);
            element.FullPath = Path.Combine(root, "ncm.targets.proj");

            var collection = new ProjectCollection();
            var evaluated = new Project(element, globalProperties: null, toolsVersion: null, projectCollection: collection);

            //the engine is never allowed to write its own log: a target that succeeds stays silent, and one that fails
            //hands the errors back so they are printed under the progress line instead of through it
            var logger = new CollectingLogger();

            var ran = 0;
            foreach (var target in PostBuildTargets)
            {
                if (!evaluated.Targets.Any(item => item.Key == target))
                    continue;

                Progress.Detail(target);
                if (!evaluated.Build(target, [logger]))
                {
                    error = logger.Errors.Count > 0
                        ? string.Join(Environment.NewLine, logger.Errors)
                        : $"target {target} failed";
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
    //The Sdk attribute stays: the engine and the sdk props and targets ship with the tool, which is what lets a package
    //target see the same evaluated context it would see under dotnet build
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

        //The same switches the direct build uses, so a package target sees one project instead of two different ones
        builder.AppendLine($"    <LangVersion>{Escape(project.Check.LangVersion)}</LangVersion>");
        builder.AppendLine($"    <DefineConstants>{Escape(project.Build.DefineConstants)}</DefineConstants>");
        builder.AppendLine($"    <Nullable>{(project.Build.Nullable ? "enable" : "disable")}</Nullable>");
        builder.AppendLine($"    <AllowUnsafeBlocks>{Flag(project.Build.AllowUnsafeBlocks)}</AllowUnsafeBlocks>");
        builder.AppendLine($"    <Optimize>{Flag(project.Build.Optimize)}</Optimize>");
        builder.AppendLine($"    <CheckForOverflowUnderflow>{Flag(project.Build.CheckForOverflowUnderflow)}</CheckForOverflowUnderflow>");
        builder.AppendLine($"    <PlatformTarget>{Escape(project.Build.PlatformTarget)}</PlatformTarget>");
        builder.AppendLine($"    <WarningLevel>{project.Build.WarningLevel}</WarningLevel>");
        builder.AppendLine($"    <NoWarn>{Escape(project.Build.NoWarn)}</NoWarn>");
        builder.AppendLine($"    <WarningsAsErrors>{Escape(project.Build.WarningsAsErrors)}</WarningsAsErrors>");
        builder.AppendLine($"    <WarningsNotAsErrors>{Escape(project.Build.WarningsNotAsErrors)}</WarningsNotAsErrors>");
        builder.AppendLine($"    <TreatWarningsAsErrors>{Flag(project.Build.TreatWarningsAsErrors)}</TreatWarningsAsErrors>");
        builder.AppendLine($"    <Deterministic>{Flag(project.Build.Deterministic)}</Deterministic>");
        builder.AppendLine($"    <DebugType>{Escape(project.Build.DebugType)}</DebugType>");
        builder.AppendLine($"    <DebugSymbols>{Flag(project.Build.DebugSymbols)}</DebugSymbols>");
        builder.AppendLine($"    <GenerateDocumentationFile>{Flag(project.Build.GenerateDocumentationFile)}</GenerateDocumentationFile>");
        builder.AppendLine($"    <EnableNETAnalyzers>{Flag(project.Build.EnableNETAnalyzers)}</EnableNETAnalyzers>");
        builder.AppendLine($"    <AnalysisLevel>{Escape(project.Build.AnalysisLevel)}</AnalysisLevel>");
        builder.AppendLine($"    <EnforceCodeStyleInBuild>{Flag(project.Build.EnforceCodeStyleInBuild)}</EnforceCodeStyleInBuild>");
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

        builder.AppendLine("    <ReferencePathWithRefAssemblies Include=\"@(Reference)\" />");

        foreach (var file in SourceFiles.Enumerate(root, "*.axaml"))
            builder.AppendLine($"    <AvaloniaXaml Include=\"{Escape(file)}\" />");
        builder.AppendLine("  </ItemGroup>");

        //Package targets are imported with their own relative layout so task assembly paths resolve through MSBuildThisFileDirectory
        foreach (var import in PackageTargets.Imports(root))
            builder.AppendLine($"  <Import Project=\"{Escape(import)}\" />");

        //The package appends its own dependency chain here, which would resolve targeting packs and project references
        //this project does not have; the reference list it reads is supplied above, so the chain is cleared after the import
        builder.AppendLine("  <PropertyGroup>");
        builder.AppendLine("    <CompileAvaloniaXamlDependsOn></CompileAvaloniaXamlDependsOn>");
        builder.AppendLine("  </PropertyGroup>");

        //Stage the assembly wherever the sdk expects intermediates instead of guessing its directory layout
        builder.AppendLine("""
              <Target Name="NcmStageAssembly" BeforeTargets="CompileAvaloniaXaml">
                <MakeDir Directories="$(IntermediateOutputPath)Avalonia" />
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

    //Points the engine at the props and targets that ship beside it, without which the Sdk attribute cannot be evaluated
    //The extensions paths must point at that same root so Microsoft.Common.props resolves, and an already set variable is left alone
    internal static void Sdks()
    {
        var sdk = MsBuildLibrary.SdkDirectory;
        if (sdk is null)
            return;

        var sdkRoot = sdk + Path.DirectorySeparatorChar;
        Keep("MSBuildSDKsPath", Path.Combine(sdk, "Sdks"));
        //workload resolution needs sdk folders that are not bundled, and a package target never asks for a workload
        Keep("MSBuildEnableWorkloadResolver", "false");
        Keep("MSBuildExtensionsPath", sdkRoot);
        Keep("MSBuildExtensionsPath32", sdkRoot);
        Keep("MSBuildExtensionsPath64", sdkRoot);
        //The engine locates SdkResolvers relative to the msbuild assembly, so pointing at this copy makes it accept the sdk directory
        var exe = Path.Combine(sdk, "MSBuild.dll");
        if (File.Exists(exe))
            Keep("MSBUILD_EXE_PATH", exe);

        Trace.Log($"msbuild root {sdk}");
    }

    //Only fills unset variables, leaving already-pointed ones untouched
    private static void Keep(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    //Flag writes a boolean the way the engine spells it
    private static string Flag(bool value)
        => value ? "true" : "false";

    //CollectingLogger keeps the engine quiet and hands the errors back, since the progress line owns the console
    //A target that succeeds prints nothing; a failed one produces the lines the caller reports
    private sealed class CollectingLogger : ILogger
    {
        private readonly List<string> _errors = new();

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;

        public string? Parameters { get; set; }

        public IReadOnlyList<string> Errors => _errors;

        public void Initialize(IEventSource eventSource)
            => eventSource.ErrorRaised += (_, e) =>
                _errors.Add($"{e.File}({e.LineNumber},{e.ColumnNumber}): {e.Code}: {e.Message}");

        public void Shutdown()
        {
        }
    }
}

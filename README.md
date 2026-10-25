# NacreCraft.DevTools

Development tools for [NacreCraft](https://github.com/NacreCraftDev/NacreCraft) mods.

| Project | Contents |
|---|---|
| `NacreCraft.ModBuild.Tools` | `ncm` — the mod development CLI, packaged as a dotnet tool |
| `NacreCraft.ModsProjectType` | The `dotnet new ncm` project template |
| `NacreCraft.NcMod.LangServer` | The C# and JavaScript language server used by the editor extension |
| `VSCode/` | The `NacreCraft NcMod` VS Code extension |
| `reference/` | Kernel reference assemblies handed to newly created projects |

## Install the toolchain

The install scripts set up everything a mod author needs — the .NET 10 SDK, the `dotnet new ncm` project template and the `ncm` CLI — downloading whatever is missing and refreshing what is already installed. They are idempotent, so rerunning one is the update path.

| Platform | Script |
|---|---|
| Windows | `tools/install.ps1` |
| Linux | `tools/install-linux.sh` |
| macOS | `tools/install-macos.sh` |

```powershell
./tools/install.ps1
```

```shell
./tools/install-linux.sh
```

Packages come from nuget.org. Then, from a mod project:

```powershell
ncm init                        # create a mod project with prompts
ncm init <name> <id> <description> <authors> <homepage> <sources> <license>
ncm init <name> --here          # create in the current directory
                                # same fields in the same order, no prompts
                                # trailing ones may be omitted, empty string skips one
ncm template example <api>      # write an example for an API type
ncm template tui                # terminal panel: grade the api usage of the current project
ncm template gui                # the same catalog and grading in a window
ncm add nuget <id> [version]    # declare a package dependency in the .ncproj
ncm add file <path>             # declare a managed dll as a compile reference
ncm add project <path>          # reference another project in the .ncproj
ncm restore                     # resolve the declared packages into Build/packages/
ncm build [--lint]              # diagnose, optionally lint, then build
ncm clean [target] [--all]      # drop the build cache, a named cache, or every cache
ncm runserver                   # build the mod and run the server
ncm runclient                   # build the mod and run the client
ncm asm <assembly>              # inspect any .NET assembly
ncm callgraph [options]         # inspect the call graph and report call cycles
ncm flow [class|file]            # show a class execution order as a tree
ncm hook                        # check the injection rules of a mod
ncm icon                        # write a white background mod icon
ncm tool add|remove|list         # install, remove and list plugins
ncm tool <plugin> <method>      # run an installed plugin
ncm upgrade                     # convert a csproj project to a .ncproj one
ncm update                      # check nuget.org, then update ncm itself

dotnet new ncm -n MyMod         # or create from the template directly
```

## Command reference

`ncm help` lists every tool, `ncm help <tool>` prints one tool's parameters, and `ncm help all` prints every tool's help in one run.

### `init`

Create a new NacreCraft mod project. The template is embedded in `ncm`, so this works without `dotnet new` or a machine-wide template install.

| Parameter | Description |
|---|---|
| `<name>` | Mod name, required |
| `[id]` | Mod id, derived from the name when omitted |
| `[description]` | Mod description |
| `[authors]` | Comma separated author names |
| `[homepage]` | Homepage url |
| `[sources]` | Source repository url |
| `[license]` | License name |
| `--here` | Create in the current directory instead of a subdirectory named after the mod |
| `--upgrade` | Convert the created csproj to a `.ncproj` right away, removing the csproj |

### `template`

Browse and pull mod templates.

| Parameter | Description |
|---|---|
| `view [pattern]` | List template entries, `?` and `*` work as wildcards |
| `example <api id>` | Pull the example file of an entry into the current directory |
| `gui` | Open the template panel in a window |
| `tui` | Open the terminal panel, grading the api usage of the project |
| `--refresh` | Clear the local cache first and pull everything again |

### `add`

Declare a dependency, a reference or an injection rule. The first three are written back to the `.ncproj`, the rule to the `hooks` array of `ncmod.json`.

| Parameter | Description |
|---|---|
| `nuget <id> [version]` | Add a nuget package, the version takes the same syntax as `dotnet add package` |
| `file <path>` | Add a managed dll as a compile reference, written as a `<File>` under `<References>` |
| `project <path>` | Add another project as a reference, written as a `<Project>` under `<References>` |
| `mod <id> [version]` | Add a mod dependency, not implemented yet |
| `hook <target> <method> <replaceType> <replaceMethod>` | Append a rule to the `hooks` array of `ncmod.json`, replacing a call site with one of this mod's methods |

`file` and `project` take a path relative to the current directory and store it relative to the project root.

The rule takes options for the fields that have a closed vocabulary, and only what was chosen is written down:

| Option | Default |
|---|---|
| `--type <form>` | `CallSite`, one of the injection forms listed under `ncm hook` |
| `--patch-mode <mode>` | `ILRewrite`, the other being `RuntimeInject` |
| `--placement <place>` | `Replace`, or `Before` / `After` to keep the anchor and insert around it |
| `--environment <side>` | `both`, or `client` / `server` |
| `--label <text>` | none, used by `Mark` and `Probe` |

A rule for the same target, method, form and patch mode is refused instead of appended, since the loader keys a rule that way and a second copy would never land. Run `ncm hook` afterwards to check the rule against the target.

```
ncm add hook NacreCraft.Game.Server.DedicatedServer Tick MyMod.Probe OnTick
ncm add hook NacreCraft.Test1.Test1Api Farewell MyMod.Probe OnFarewell --type Mark --label farewell --environment server
```

### `tool`

Install, remove and run plugins. A plugin is a class library: the name is the folder it lives in, the method picks an entry point on the type carrying that name (as its own name or as its full name, so `MyPlugin.Tools.dll` can stand for the class `Tools` in the namespace `MyPlugin`), and that entry point has to be a `public static int` method taking a `string[]`, with its return value becoming ncm's exit code.

```
ncm tool add out/MyPlugin.dll    # copies the dlls beside it into the tool cache
ncm tool list                    # MyPlugin
ncm tool MyPlugin Build --release
ncm tool remove MyPlugin
```

| Parameter | Description |
|---|---|
| `add <dll>` | Install a plugin: every dll beside the given one is copied into the tool cache under the dll's own name, and installing twice overwrites. Needs no `.ncproj`, a plugin belongs to the machine |
| `remove <plugin>` | Remove an installed plugin |
| `list` | List the installed plugins, one per line |
| `<plugin> <method> [args]` | Run the plugin: the entry point to call is one of its public static int methods taking `string[]`, and the other usable ones are listed when the name is wrong. Everything after the method is passed through unchanged |

`add`, `remove` and `list` are taken as subcommands, so a plugin cannot be named after one of them.

Plugins run inside the ncm process, so they reach everything ncm reaches: there is no sandbox, and one that calls `Environment.Exit` takes ncm down with it. Installing does not check the entry point either, so a plugin whose shape is wrong only says so when it is called.

### `restore`

Resolve the `<Packages>` declared in the project config and copy their assemblies and build files into the project, under `Build/packages` and `Build/targets`. `build` runs the same step on its own, so this is only needed when the restored files are wanted without a build.

| Parameter | Description |
|---|---|
| `[directory]` | Where to restore, relative to the project root, defaults to `Build/packages` |

### `build`

Diagnose and build the mod project in the current directory. The kernel reference assemblies under `Build/kernel` and the declared packages are laid down first, whatever is already there is kept, and only the missing files are fetched. Referenced projects are built too: one that is itself a mod is copied beside the output, so the mods directory picks it up, and anything else is embedded into the assembly as an embedded dependency. Before compiling, the api usage, C# syntax and injection rules are checked against the same references the runtime uses, so a rule an outdated kernel can no longer satisfy fails the build instead of loading. Then the project is compiled, the build targets the packages ship are run, and the result is deployed.

With `--lint` the Roslynator code quality rules run over the same compilation after the regular checks. They are off by default, hidden editor-only hints are skipped, and the output is capped at 500 diagnostics.

With `--taint` every method parameter is treated as untrusted and followed to a dangerous api, which is reported as `NC0005` beside the flat `NC0003` hit. The report carries the call chain, so a value that is only handed to `Marshal` after passing through two helpers no longer looks like a plain api usage.

| Parameter | Description |
|---|---|
| `-c`, `--configuration <name>` | Build configuration, defaults to `Release` |
| `--no-check` | Skip the api, syntax and injection checks and run `dotnet build` directly |
| `--check-only` | Run the api, syntax and injection checks only, build nothing |
| `--no-manifest` | Skip the `ncmod.json` lookup, treat the current directory as a plain C# project (must contain a csproj). Its assembly, project and package references are resolved through msbuild, restoring it first when needed |
| `--lint` | Run the Roslynator code quality rules in addition to the api and syntax checks |
| `--taint` | Follow untrusted input to a dangerous api after the regular checks |

### `clean`

In a project this removes its build cache, `Build`, and leaves the downloads alone: the next build lays it down again. Outside a project the only thing there is to remove is the shared download cache, so ncm says where it is and how big it is and asks first.

Everything ncm keeps for itself lives under the user directory — `%LOCALAPPDATA%\NacreCraft\ncm` on Windows, `~/.local/share/NacreCraft/ncm` on Linux, `~/Library/Application Support/NacreCraft/ncm` on macOS — where `Client/`, `Shared/`, `ClientRuntime/`, `Server/`, `Runtime/`, `Template/`, `packages/` and `Update/` are the caches ncm can lay down again, and `Tool/` holds the plugins you installed. Updating or reinstalling ncm leaves all of it alone, and every project on the machine reuses the same copy. `NCM_CACHE_ROOT` points all of it somewhere else, which a test run or a shared machine may want.

A release is split into three bundles: `Shared/` holds the files the client and the server carry byte for byte, `ClientRuntime/` and `Server/` hold only what differs, and `Runtime/` is the merged folder a launch reads — the shared files first, the side's own linked over them.

| Parameter | Description |
|---|---|
| `[target ...]` | Remove the named caches: `client`, `assets`, `shared`, `server`, `clientruntime`, `runtime`, `template`, `packages`, `update`. The `server` target is the cache ncm downloads into, never a local kernel folder given to `<Sources Directory>`, which is the project's own copy. Several may be named, and nothing is asked since it was asked for. Works in and outside a project |
| `--all` | Remove every cache without asking: the client jar, the asset objects, the shared and per-side runtimes, the template files, the packages and the update package. Installed plugins are not cache and are kept, and in a project the build cache goes as well |

### `runserver`

Build the current mod and run the NacreCraft server. The runtime is the shared bundle plus the server half, merged into the cache before the launch. On the first run of a project it downloads the Minecraft client jar from the remote index and passes it to the server as `--jar-path`, so the terrain and asset data land in the run directory.

| Parameter | Description |
|---|---|
| `--refresh` | Refresh the server cache against the remote index, no `ncmod.json` and no build needed. A project on a local kernel directory has nothing to refresh and is told as much |
| `[server args]` | Everything after `runserver` is passed to the server unchanged |

### `runclient`

Build the current mod and run the NacreCraft client. It shares the `run` folder with `runserver` and prepares the same staged mods, plus the resources the client jar cannot supply. The client runtime is the shared bundle plus the client half, so it carries the client program itself and only downloads what differs from the server.

The vanilla jar carries no `pack.mcmeta`, only the `en_us` language table and none of the sounds, so the rest comes from the resource index the launcher downloads alongside the version. Those objects are kept once per machine under the user cache and shared by every project, and only the missing ones are fetched:

- `--assetsDir` points at that shared directory, whose `indexes` and `objects` hold the content addressed files
- `--assetIndex` names the index the kernel lands from, which is where the language tables, the fonts and `pack.mcmeta` come from
- `--sounds-dir` hands over a plain copy of the sound entries, since the kernel reads audio from a directory rather than from the object store

On the first run of a project the Minecraft client jar is downloaded too and passed as `--jar-path`; afterwards the extracted assets stay in `run`.

| Parameter | Description |
|---|---|
| `--refresh` | Refresh the kernel cache against the remote index, no `ncmod.json` and no build needed |
| `[client args]` | Everything after `runclient` is passed to the client unchanged |

### `asm`

Inspect a .NET assembly: type list, type details, decompiled source, dependencies, embedded resources. A resource can also be taken out, and a file embedded into an assembly.

| Parameter | Description |
|---|---|
| `<assembly>` | Path to a .NET assembly |
| `-t`, `--type <name>` | Show type details |
| `-d`, `--decompile <name>` | Decompile a type to C# source |
| `-dep`, `--dependencies` | List assembly dependencies |
| `-res`, `--resources [name]` | List the embedded and linked resources, or extract one when a name is given |
| `-ar`, `--add-resource <file>` | Embed a file into the target assembly as a resource, repeatable |
| `-rr`, `--remove-resource <name>` | Remove a resource from the target assembly, repeatable |
| `-r`, `--reference <dir>` | Extra directory to look for dependencies, repeatable |
| `-o`, `--output <file>` | Write to a file: the text output, or the extracted resource |

A type may be named by its short name, its full name, or its nested form. Nested types resolve as `Namespace.Outer.Inner`, `Namespace.Outer+Inner` or `Namespace.Outer/Inner`, and a name without its namespace resolves when the trailing part is unique.

A resource name may be shortened to a unique trailing part. Extraction without `-o` lands in the current directory under the resource name.

`-ar` names the resource after the file, which is how the loader matches an embedded dependency, and `ncmod.json` is the mod declaration it recognizes a mod by. A name the target already carries is replaced, so pointing at an existing resource is also how a replacement is done. Embedding rewrites the assembly, which drops a strong name signature.

`-rr` takes a resource back out, matched by full name or a unique trailing part. Every name is resolved before the file is rewritten, so a name that matches nothing leaves the assembly untouched. Removing works the same way as embedding and also drops a strong name signature.

```
ncm asm Build/kernel/NacreCraft.Game.dll -res NacreCraftGame.World.Level.Block.blocks.txt -o blocks.txt
ncm asm Build/kernel/NacreCraft.Game.dll -res blocks.txt      # same resource, into the current directory
ncm asm Build/out/MyMod.dll -ar Build/packages/MyLib.dll    # embed a dependency into a built mod
ncm asm Build/out/MyMod.dll -rr MyLib.dll                   # drop it again
```

### `callgraph`

Scan the project sources and print the call graph: the entry methods, the conditional branches, the call ends, and the cycles that never leave. The graph is built from the same Roslyn compilation the checks use, so it follows the ncproj exclusions and references.

| Parameter | Description |
|---|---|
| `-p`, `--project <dir>` | Project directory to inspect, defaults to the current directory or its nearest `.ncproj` parent |
| `-r`, `--root <method>` | Start from this method instead of the detected entries, repeatable |
| `--no-ncproj` | Ignore the ncproj config, scan every source file and allow a plain directory |
| `--exclude <pattern>` | Skip source paths matching this pattern, repeatable, combined with the ncproj `Exclude` |
| `--depth <n>` | Limit the call depth, `0` means no limit |
| `--no-external` | Hide calls that leave the project sources |
| `--no-cycles` | Skip the call cycle section |
| `--no-color` | Disable ANSI colours in the console |
| `-o`, `--output <file>` | Write the report to a file instead of the console |

An entry is a method with no incoming call, `Main` first. A conditional call carries its test as `[if ...]`, `[else]`, `[&&]` or `[?.]`; a call back into a method still being expanded is `[recursive]`, a call outside the sources is `[external]`, and a branch cut by `--depth` is `[depth]`. Calls to the same target under one caller are merged with `xN`, and a body is expanded once per report so later references say `[expanded]`.

The cycle section lists the calls that have no exit and whose condition never flips. `build` reports the same set as `NC0004`, so a clean `callgraph` and a passing check agree. A walk is capped at 256 levels and 20000 nodes, which keeps a very wide graph bounded.

```powershell
ncm callgraph --no-external
ncm callgraph -r MyMod.Entry.OnLoad --depth 3
ncm callgraph -o callgraph.txt
```

### `flow`

Show the execution order of a class as a tree, without reading the C#. Each method's statements are listed in order, with branches, loops, `try`/`catch`, `using`, `lock` and local functions nested below the step that owns them. The outline is built from the same Roslyn operations the call graph uses.

| Parameter | Description |
|---|---|
| `-p`, `--project <dir>` | Project directory to inspect, defaults to the current directory |
| `-f`, `--file <path>` | Source file to inspect, repeatable, defaults to every project source |
| `-c`, `--class <name>` | Class to show, defaults to every class in the selected files |
| `--no-ncproj` | Ignore the ncproj config, scan every source file and allow a plain directory |
| `--exclude <pattern>` | Skip source paths matching this pattern, repeatable |
| `--depth <n>` | Limit nested statement depth, `0` means no limit |
| `--all` | Expand every class body, not only the class named with `--class` |
| `--no-color` | Disable ANSI colours in the console |
| `-o`, `--output <file>` | Write the outline to a file instead of the console |

Without `--class` the command lists the classes and their method signatures; naming a class expands its method bodies. A path ending in `.cs` is taken as a file, so `ncm flow Main/Site/AccountAdminEndpoints.cs` and `ncm flow AccountAdminEndpoints` both work. Steps use the same tree branches as the call graph: `├─` when another step follows, `└─` for the last one, and nested blocks carry `│` under their parent.

```powershell
ncm flow AccountAdminEndpoints
ncm flow -f Main/Site/AccountAdminEndpoints.cs
ncm flow --all -o flow.txt
```

### `taint`

Follow untrusted values from a root method to a sink and print every route that carries one there. The walk uses the same Roslyn operation tree as the call graph, so it follows a value through locals, assignments, fields, properties, arguments and local calls — which is what a text search over the sources cannot do.

| Parameter | Description |
|---|---|
| `-p`, `--project <dir>` | Project directory to inspect, defaults to the current directory |
| `-f`, `--file <path>` | Source file to inspect, repeatable, defaults to every project source |
| `-c`, `--class <name>` | Class to inspect, repeatable |
| `-m`, `--method <name>` | Method to inspect, repeatable |
| `--no-ncproj` | Ignore the ncproj config, scan every source file and allow a plain directory |
| `--exclude <pattern>` | Skip source paths matching this pattern, repeatable |
| `-r`, `--root <method>` | Root method whose parameters are untrusted, repeatable, defaults to every method in scope |
| `--root-parameter <name>` | Only treat this parameter of a root as untrusted, repeatable; every parameter is seeded when omitted |
| `--source <pattern>` | Treat a matching method as a source: what it returns is untrusted, repeatable |
| `--sink <pattern>` | Treat a matching method as a sink, repeatable |
| `--sanitizer <pattern>` | Treat a matching method as a sanitizer: what it returns is clean, repeatable |
| `--rules <file>` | Json file with `sources`, `sinks` and `sanitizers` arrays |
| `--depth <n>` | Interprocedural depth, defaults to `8`, `0` means the internal limit |
| `--interprocedural <mode>` | `none` or `summary`, defaults to `summary` |
| `--no-fields` | Do not follow fields and properties |
| `--no-external` | Stop propagation at the project boundary instead of following calls out of it |
| `--max-paths <n>` | Stop after this many paths, defaults to `200` |
| `--no-color` | Disable ANSI colours in the console |
| `-o`, `--output <file>` | Write the report to a file instead of the console |

A pattern matches a method by full name, by type and method name, or by method name alone, with `*` and `?` as wildcards; `System.IO.File.*` therefore covers every `File` overload. The dangerous apis the syntax check already knows are sinks on their own, so `--sink` is for the calls this project considers dangerous rather than for repeating `NC0003`.

Every parameter of a root is seeded by index, so a value handed to the second parameter is not mistaken for the first; `--root-parameter` narrows that to the names given, which is how a call whose other arguments are trusted is checked. A `summary` run follows local calls and stops on a cycle; `none` stays inside the root. Each report entry is the call chain and the sink site, and the same chain at the same position is reported once.

```powershell
ncm taint -r MyMod.Entry.OnLoad --sink "System.IO.File.*" --sanitizer "System.Net.WebUtility.HtmlEncode"
ncm taint --rules taint.json --interprocedural none -o taint.txt
ncm taint -c MyMod.Net.Handler --no-fields
```

`build --taint` runs the same analysis over every source and reports `NC0005`, and `hook` runs it over each replacement method, since a replacement receives whatever the original received.

Fields and properties are tracked per object, not per symbol: every `new` gets a slot that stays the same across calls, so a field written on one instance does not mark another, and an object handed to a method carries what was written into it. A slot whose constructor took untrusted arguments counts as untrusted as a whole, since that state cannot be read back. Static fields belong to no instance and are shared by the run.

Collection elements are followed loosely, the way CodeQL models element content without the index: a write method (`Add`, `Insert`, `Append`, `Write`, `Set`, `Enqueue`, `Push` and the like) marks its receiver when an argument is untrusted, and reading anything back out of that receiver — an indexer, `foreach`, `ToString` — counts as untrusted. Writing one element therefore marks the whole collection.

A call through an interface or a virtual method is resolved to what can actually run there. The receiver type is known when the object came from a `new` in the same walk, which narrows the call to the one implementation; when it is unknown, every implementation in the sources is followed and their results are merged. A contract with more than 8 implementations, or a method declared on `object`, falls back to the declaration so a wide interface cannot explode the walk.

### `batch`

Run several tasks in one process. Each task is a complete command line without the `ncm` prefix, and `&` separates them. The project and the global config are loaded once, so repeated checks do not pay the startup cost again while every task still starts from a clean state.

| Parameter | Description |
|---|---|
| `--continue-on-error` | Keep going after a task fails instead of stopping |

An `&` inside double or single quotes belongs to the argument rather than separating tasks, and `\&` escapes one outside quotes. A task that exits non-zero stops the run with that code; with `--continue-on-error` the remaining tasks still run and the first failure is returned at the end.

```powershell
ncm batch 'taint -r MyMod.Entry.OnLoad --sink "System.IO.File.*"&callgraph --no-external'
ncm batch --continue-on-error 'build --check-only&hook'
```

### `hook`

Check the injection rules of a mod before the server ever loads them: every `hooks` or `mixins` entry in `ncmod.json` and every `[Inject]` / `[Mixin]` annotation in the sources. The C# syntax check runs first, since a rule cannot be read out of sources that do not compile.

| Parameter | Description |
|---|---|
| `--project <dir>` | Check the project in that directory instead of the current one |

What it verifies: the hook type, patch mode, placement and environment are known spellings, the target type and method exist among the kernel assemblies and the referenced projects, the replacement class and method exist in this project, and the replacement signature can carry what the form hands it. A `CallSite` or `MethodBody` replacement takes the target's parameters, with its receiver first for an instance method; `Mark` is called as `void M(string label)`; `LocalRead` and `Constant` take nothing; `LocalWrite` takes the written value; `CallArg` takes the host parameters and the original value. Anchors a form cannot work without are required as well: `LocalIndex` for `LocalRead`/`LocalWrite`, `ConstantValue` for `Constant`, `ArgumentIndex` for `CallArg`.

A rule written as an annotation cannot exist in a project that sets `DependsOnModApi="false"`, since the annotation type comes from the mod api; that combination is reported on its own rather than left to surface as a missing type.

Rules whose `patchMode` is `RuntimeInject` are counted and left alone, since that route has its own limits.

The manifest is read with a reader that keeps line and column on every token, so a report points at the entry that is wrong rather than at the file as a whole: a manifest that is not valid json is reported where it broke (with the usual cause spelled out), and each rule is checked for fields that are not part of the rule table and for values of the wrong kind.

New rules are appended with [`ncm add hook`](#add), and a rule that the loader would refuse for the same key is not written a second time.

```
ncm hook                        # check the project in the current directory
ncm hook --project ../OtherMod  # check another one
```

### `icon`

Generate a white background mod icon with the mod name.

| Parameter | Description |
|---|---|
| `-f`, `--force` | Overwrite an existing icon without asking |

### `upgrade`

Convert a csproj based mod project to a `.ncproj` one. What ncm does not understand is listed so it can be handled by hand; the original csproj is renamed to `.bak` unless `--no-backup` is given.

| Parameter | Description |
|---|---|
| `[file]` | The csproj to convert, defaults to the only csproj in the current directory |
| `--no-backup` | Delete the original csproj instead of keeping a `.bak` copy |

### `update`

Check nuget.org and update ncm to the latest version. A newer package is downloaded into `Update/` first, then a script there runs `dotnet tool update` against that local copy, so the install itself does not depend on nuget.org being reachable. Takes no parameters.

## Project config

A mod project can carry a `.ncproj` file next to `ncmod.json`. It holds development-time settings only — how the project is built, run and packaged — and is never embedded into the mod, unlike `ncmod.json`, which the loader reads at runtime.

With a `.ncproj` present ncm compiles the project with Roslyn directly; without one it falls back to `dotnet build`. Tasks declared here run as `ncm <task>`.

Everything the build needs at build time lives under `Build`: `Build/kernel` holds the NC reference assemblies synced from the kernel cache, or from the local kernel directory when `<Sources>` names one, `Build/packages` the packages pulled by the restore, `Build/targets` the props and targets those packages ship, `Build/obj` the intermediate directory the build targets run in, and `Build/out` the build output.

```xml
<ncproj version="1">
  <Check LangVersion="latest" />
  <Build AssemblyName="Demo" Configuration="Release" Output="Build/out" OutputType="Library"
         RootNamespace="Demo" Nullable="true" ImplicitUsings="true" DefineConstants="DEBUG" ExtraArgs="" />
  <Packages>
    <Package Id="Newtonsoft.Json" Version="13.0.3" />
  </Packages>
  <References>
    <File Include="libs/**.dll" />
    <Project Include="../SharedLib" />
  </References>
  <Server Cache="cache" Args="--nogui" Debug="false" />
  <Client Version="26.2" Jar="https://example.com/client.jar" Args="--username dev" />
  <!-- Directory names a local kernel folder and stands in for the download cache, Url is used when it is not there -->
  <!-- Url points at the server folder; shared and client are taken from the sibling folders of the same name -->
  <Sources Url="https://example.com/kernel/" Index="index.txt" Format="sha256-lines" />
  <Template Url="https://example.com/NacreCraftTemplate.yaml" Base="https://example.com/NacreCraftTemplate" />
  <InternalsVisibleTo>
    <Assembly Name="NacreCraft.Test" />
  </InternalsVisibleTo>
  <AvaloniaResources>
    <Resource Include="Gui/Assets/**" />
  </AvaloniaResources>
  <EmbeddedResources>
    <Resource Include="assets/**" />
    <Resource Include="Fonts/icon.ttf" LogicalName="icon.ttf" />
  </EmbeddedResources>
  <Deploy To="run/mods;../host/mods" />
  <!-- kept out of the compile source list, the checks and the axaml pick up -->
  <Exclude Include="legacy/**;third_party/**" />
  <Tasks>
    <Task Name="release" Description="build and pack" Depends="check">
      <Exec>ncm build</Exec>
      <Copy From="Build/*.dll" To="dist/" />
      <Zip From="dist" To="dist/$(ModId)-$(ModVersion).zip" />
      <!-- optional, only for a plugin installed with 'ncm add tool' -->
      <Tool Name="MyPlugin" Method="Pack" Args="--out dist" />
    </Task>
  </Tasks>
</ncproj>
```

| Element | Attributes |
|---|---|
| `Check` | `LangVersion` — the C# version used for the api and syntax checks |
| `Build` | `AssemblyName`, `Configuration`, `Output`, `OutputType` — `Library` (the default) or `Exe`, a plain compilation choice, ncm writes neither an apphost nor a runtimeconfig, `RootNamespace` — used as the default prefix of embedded resource names, `Nullable`, `ImplicitUsings`, `DefineConstants`, `ExtraArgs`, `DependsOnModApi` — whether the mod api is restored as a compile reference and staged next to the mod, `true` by default; set it to `false` for a pure mod that does not use the api, `AllowUnsafe` — the equivalent of the csproj `AllowUnsafeBlocks`, `false` by default; a mod that turns it on also has its `unsafe` code measured by the memory safety check |
| `Packages` | one `Package` per dependency with `Id` and an optional `Version`, the version range syntax matches NuGet |
| `References` | `File` — a dll path or pattern, `*`, `?` and `**` work as wildcards, used as a compile reference only; `Project` — the directory or the project file of another project, ncproj or csproj, built first so its output is up to date, then what it produced is copied beside the built assembly when it is itself a mod, and embedded into the assembly as an embedded dependency when it is not. Both paths are resolved relative to the project root |
| `Server` | `Cache` — where the runtime files are cached, the shared download cache by default, `Args` — extra server arguments, `Debug` — always run in debug mode |
| `Client` | `Version` — the client jar to fetch, `Jar` — a direct download url instead of the version manifest, `Args` — extra client arguments |
| `Sources` | `Directory` — a local kernel folder resolved against the project root, taking the place of the cache: the build and `runserver` both read the kernel from there, and it is synced again on every build so an edited kernel shows up at once. A server build output works as it stands, the assemblies in its `kernel` subfolder being read along with the ones beside it; a folder that is not there falls back to `Url` with a warning. `Url` — where the kernel is fetched from, used as it stands so a mirror prefix can be glued in front, `Index` — the manifest file name, `Format` — `sha256-lines`, `plain` or `regex`, `Pattern` — the two capture groups, hash then path, of the regex form |
| `Template` | `Url` — where the template catalog is fetched from, `Base` — the base the example files are pulled from, replacing the one written in the catalog; either may be left out, and both take a mirror prefix the same way `Sources` does |
| `InternalsVisibleTo` | one `Assembly` per friend assembly with `Name`, emitted as `[assembly: InternalsVisibleTo]` |
| `AvaloniaResources` | one `Resource` per pattern with `Include`, packed into the `!AvaloniaResources` resource the Avalonia asset loader reads; every `*.axaml` of the project is picked up as well, so only plain assets have to be listed |
| `EmbeddedResources` | one `Resource` per pattern with `Include` and an optional `LogicalName`; every matched file is embedded into the built assembly. Without `LogicalName` the resource name is the root namespace plus the file path relative to the project root, slashes turned into dots, so files outside the project root need an explicit one |
| `Deploy` | `To` — semicolon separated directories the built dll is copied into right after a successful build |
| `Exclude` | `Include` — semicolon separated patterns kept out of everything ncm scans on its own: the compile source list, the api and syntax checks and the axaml pick up. Patterns are relative to the project root, `*` and `?` stay within one segment and `**` crosses directories. A pattern without a wildcard also covers everything under it, one with a wildcard has to match the whole path — see the note below the table. Patterns declared elsewhere, such as `<References>` or `<AvaloniaResources>`, are still honored as written, so an excluded directory can still be referenced on purpose |
| `Tasks` | one `Task` per task with `Name`, `Description`, `Depends` and `Override`, holding `Exec` steps that run a command line, `Copy` steps that take `From` and `To`, `Zip` steps that pack the `From` directory into the `To` file, and `Tool` steps that take `Name`, `Method` and `Args` and call an installed plugin in this process, and `Script` steps that run JavaScript written inline or named through `File`. `$(Configuration)`, `$(ProjectDir)`, `$(ModId)`, `$(ModName)`, `$(ModVersion)` and `$(env:NAME)` are substituted in every attribute |
| root | `Override` — the default for tasks that do not carry their own; `Bootstrap` — `true` marks a config that aggregates other projects instead of holding sources |

### Bootstrap configs

A workspace may hold several unrelated mods. Point the workspace at one `.ncproj` with `Bootstrap="true"`; it carries no sources and needs no `ncmod.json`, and the projects it references with the usual `<References><Project .../></References>` become its workspace.

- `ncm` lists their tasks as `<config>/<task>`, where `<config>` is the config file name without `.ncproj`, and runs them in the owning project. A task declared on the bootstrap itself keeps its plain name.
- The language server loads the referenced projects through the same config; see [Editor support](#editor-support).
- A bootstrap cannot reference another bootstrap, and the error names both files.

```xml
<ncproj version="1" Bootstrap="true">
  <References>
    <Project Include="Mods/First" />
    <Project Include="Mods/Second" />
  </References>
</ncproj>
```

### `Exclude` patterns

A pattern is relative to the project root and is matched against whole paths. Without a wildcard it also covers everything under the named directory; with a wildcard it does not, so a directory whose name needs one has to be written with `/**` after it. Several `<Exclude>` elements add up.

```xml
<Exclude Include="legacy" />      <!-- the directory legacy and everything under it -->
<Exclude Include="legacy/**" />   <!-- the same thing -->
<Exclude Include="_rev*" />       <!-- only a root entry named _rev... , never a directory's contents -->
<Exclude Include="_rev*/**" />    <!-- every directory whose name matches, and everything under it -->
<Exclude Include="_rev**" />      <!-- anything whose path starts with _rev, files and directories alike -->
```

## JavaScript tasks

A task can run JavaScript with a `<Script>` step instead of a shell command. The script runs in the ncm process, with the project config and the shared ncm helpers exposed as objects. A task chain works the same way as with any other step:

```xml
<Tasks>
  <Task Name="verify" Description="build then report" Depends="build">
    <Script><![CDATA[
      log.info("hello");
      log.info("checked " + project.sources.length + " source files");
    ]]></Script>
  </Task>

  <Task Name="build" Description="compile the mod">
    <Exec>ncm build</Exec>
  </Task>
</Tasks>
```

The source can be written inline, or a `File` attribute can point at a script relative to the project root:

```xml
<Script File="tools/check.js" />
```

The api surface:

- `log.info(message)`, `log.warn(message)`, `log.error(message)` — write one line to the task output.
- `project.dir`, `project.path`, `project.name`, `project.modId`, `project.modVersion`, `project.configuration` — where the task runs and what the project is.
- `project.build.assemblyName`, `project.build.output`, `project.build.outputType`, `project.build.nullable`, `project.build.allowUnsafe`, `project.build.langVersion` — the build settings.
- `project.tasks`, `project.packages`, `project.sources` — the declared tasks, package references and source files.
- `project.manifest` — the values of `ncmod.json`, `null` when the folder has none.
- `fs.read(path)`, `fs.write(path, text)`, `fs.exists(path)`, `fs.glob(pattern)`, `fs.list(pattern)`, `fs.mkdir(path)`, `fs.remove(path)`, `fs.copy(from, to)` — file access, kept inside the project directory.
- `path.join(left, right)`, `path.dirname(path)`, `path.basename(path)`, `path.extname(path)`, `path.absolute(path)`, `path.relative(from, to)` — path arithmetic.
- `env.get(name)`, `env.names()` — environment variables.
- `hash.sha256(text)`, `hash.sha256File(path)` — lowercase hex digests.
- `xml.select(text, xpath)`, `xml.root(text)` — xml nodes with `name`, `value`, `attributes` and `attribute(name)`.
- `syntax.query(kind)` — every syntax node of that Roslyn kind, each with `kind`, `file`, `line`, `column`, `name` and `text`.
- `syntax.walk(kind, callback)` — visit the matching nodes one at a time instead of building the whole list in the script.

Scripts are interpreted by Jint, so a tight loop is slow; keep heavy analysis in ncm and use the script for rules and orchestration. A script is limited to 30 seconds, ten million statements, 256 MB of memory and a recursion depth of 128. A syntax error or an exception reports its file, line and column.

CLR access is off by default. Add `AllowClr="true"` to `<Tasks>` when a script needs the full CLR:

```xml
<Tasks AllowClr="true">
  <Task Name="info" Description="read the machine name">
    <Script><![CDATA[
      log.info(System.Environment.MachineName);
    ]]></Script>
  </Task>
</Tasks>
```

With `AllowClr` on, the `fs` restriction no longer applies: the script can reach every file and process the tool can.

## Global config

One config can serve every project on the machine: `Config.ncproj` sits beside the `Server/`, `Client/` and `Update/` caches — `%LOCALAPPDATA%\NacreCraft\ncm` on Windows, `~/.local/share/NacreCraft/ncm` on Linux, `~/Library/Application Support/NacreCraft/ncm` on macOS. ncm writes a commented template there on first use. The file is never required: absent or empty, every command behaves exactly as it did before.

It is not a project, and a project is always the more specific of the two. What it declares fills in what a project leaves out:

- The pull rules — `Sources`, `Server`, `Client`, `Template` — fall back field by field, so a project that writes only its own mirror still takes the kernel directory, the index rules and the rest from the machine.
- Every other element falls back whole: a project without a `<Build>` or a `<Packages>` uses the global one.
- `Tasks` declared here are global commands. They are listed under `Global tasks` in `ncm help` and run from any directory as `ncm <name>`; their `$(ProjectDir)` is the directory the command was issued from, so a shared task can act on the current project. A project task of the same name wins, and a name that collides with a built-in tool needs `Override="true"` just like a project task does.

Paths inside the global config resolve against its own directory, so a shared `<Deploy>` or `<References>` has to be absolute. Relative ones that only make sense in a project do not belong there.

`ncm upgrade` reads it as well: what the machine declares is written into the `.ncproj` it generates, so a converted project starts from the same source, mirror and compile options as the rest instead of changing behaviour the day the global file does.

```xml
<ncproj version="1">
  <!-- the kernel, the mirrors and the run arguments every project here shares -->
  <Sources Directory="D:\NacreCraft\kernel" />
  <Server Args="--nogui" />
  <Client Version="26.2" />

  <Tasks>
    <Task Name="check" Description="check every project without building">
      <Exec>ncm build --check</Exec>
    </Task>
  </Tasks>
</ncproj>
```

## Editor support

The `VSCode/` extension (`NacreCraft NcMod`) starts the language server and adds the project and task views, a settings page and the `.ncproj` language support. It activates when the workspace contains a `.ncproj` or `ncmod.json`.

- C# and JavaScript completion, including buffers that are not saved yet. A completion request never starts a full build unless `completion.build` is set to `on`; it is `off` by default.
- Diagnostics for C# syntax, the mod API catalog, hook rules and JavaScript syntax, published as the file changes.
- `project.load` decides how many projects are loaded: `onDemand` (default) loads a project when its `.ncproj` is opened or edited, together with its project references; `all` loads every project in the workspace at startup. A bootstrap config loads the projects it references.
- Script completion and checking use the language server's `nacrecraft/scriptComplete` and `nacrecraft/scriptCheck` requests; project listing and loading use `nacrecraft/projects` and `nacrecraft/projectLoad`.

The extension settings live in its own `config.json` under the extension storage and are edited from the settings page, not from `settings.json`.

`VSCode/build.ps1` publishes the language server, compiles the extension and packs the `.vsix`.

## License

Apache-2.0 — see [LICENSE](./LICENSE).

Roslynator rules shipped under `NacreCraft.ModBuild.Tools/Analyzers/` are Apache-2.0; see `LICENSE.txt` and `NOTICE.md` beside them.

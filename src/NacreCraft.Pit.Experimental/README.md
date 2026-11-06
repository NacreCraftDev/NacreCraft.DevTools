# Experimental command line

A second command line entry point that references `NacreCraft.Pit` and adds commands
that are not ready for release. Experiments are temporary by nature: they can be tried, rewritten
or deleted without touching what users install.

## Relationship to pit

- The project references the shipped tool project and calls `ToolRegistration.RegisterAll()`, so
  every stable tool and every project task runs from here unchanged.
- `ExperimentalRegistration` adds the commands under `Commands/` on top of the stable set.
- The core project grants `InternalsVisibleTo` for this assembly, which is what lets the
  experimental entry point reuse dispatch and the task runner instead of copying them.
- `ToolFilter` unregisters the stable tools that belong to the mod project lifecycle: icon, init,
  template, add, restore, build, clean, hook, runserver, runclient, upgrade and update. What stays
  is the analysis set (asm, callgraph, flow, taint, batch, tool) plus the experimental commands.
- Nothing here is packed or published. The core package never references this project.

## Layout

| Path | Role |
| --- | --- |
| `Program.cs` | entry point: console setup, msbuild attach, registration, help, project lookup |
| `CommandLine.cs` | dispatch: project tasks, global tasks, then the shared tool registry |
| `ExperimentalRegistration.cs` | one line per experimental command |
| `Commands/` | the experiments themselves, one file per command |
| `Java/` | Java call graph model, FastParser builder and renderer |

`Commands/SelfCheckCommand.cs` is the skeleton's smoke test. It prints the loaded versions, the
tool count and the working directory, which proves the project reference and the shared registry
are wired up.

## Install

```powershell
dotnet tool install -g NacreCraft.Pit.Experimental
```

The package bundles the core project instead of depending on it, so it installs and runs on its
own and lives beside pit. The command name is `pit-exp`.

## Running

The build prints its help under the short name `pit-exp`, because the assembly name is too long
for a usage line. From a checkout:

```powershell
dotnet run --project NacreCraft.DevTools/src/NacreCraft.Pit.Experimental -- selfcheck
```

Without arguments it prints a banner and the tool list. Any stable tool works the same way:

```powershell
dotnet run --project NacreCraft.DevTools/src/NacreCraft.Pit.Experimental -- build --check-only
```

## java-callgraph

Prints the call tree of a Java source tree. It takes the same options as the shipped `callgraph`
and differs in two ways:

- FastParser (tree-sitter) parses the sources instead of Roslyn.
- There is no cycle check. The report is the tree only.

```powershell
dotnet run --project NacreCraft.DevTools/src/NacreCraft.Pit.Experimental -- java-callgraph -p path/to/src --no-color
```

Notes:

- A declared `main` is used as the entry. When the tree declares none, the methods nothing calls
  stand in, capped at 200 so a large library stays readable.
- Java carries no type information at a call site, so a call on a local is resolved by method name
  and argument count. When several methods match, the call is counted as `unresolved` instead of
  being guessed.
- Constructors render as `Type.<init>`.
- A file the parser cannot handle is skipped and counted in a note. A `package-info` file declares
  no type and is not counted.

## java-flow

Prints the execution order of a Java class as a structured outline, the Java counterpart of the
shipped `flow`. It takes the same options, minus `--no-ncproj` because a Java tree has no project
config.

```powershell
dotnet run --project NacreCraft.DevTools/src/NacreCraft.Pit.Experimental -- java-flow -c Demo -p path/to/src --no-color
```

Notes:

- Without `-c` or `--all` only classes and method signatures are listed. A body is expanded on
  request, the same way `flow` behaves.
- Labels follow `flow`: IF / ELSE / LOOP / SWITCH / CASE / TRY / CATCH / FINALLY / CALL / NEW /
  SET / DECLARE / RETURN / THROW. Java specifics add LOCK for `synchronized`, ASSERT, LAMBDA and
  LABEL.
- Enum methods sit inside `enum_body_declarations` and are picked out from that layer.

## Adding an experiment

1. Add a class under `Commands/` with a `Register()` method that calls `ToolRegistry.Register`.
2. Add one line for it in `ExperimentalRegistration.RegisterAll()`.
3. Keep the command name distinct from the stable tools; the registry finds the first match and
   registration is append only.

## Rules

- Stable behavior belongs in the core project. This project is for things that are not ready.
- The experimental build may print extra warnings and may change or disappear at any time.
- A command that proves useful should move to the core project and be registered in
  `ToolRegistration`, not stay here.

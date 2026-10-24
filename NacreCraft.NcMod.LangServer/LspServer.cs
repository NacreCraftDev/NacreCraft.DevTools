using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using NacreCraft.ModBuild.Tools;

namespace NacreCraft.NcMod.LangServer;

//LspServer speaks JSON-RPC over stdio with the editor
//Only the handful of messages the inspection needs is handled: initialize, document events, shutdown and exit
internal sealed class LspServer
{
    private readonly Stream input;
    private readonly Stream output;

    //Documents the editor currently has open, keyed by uri
    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);

    //Language id of each open document, so a javascript buffer can be routed without reading the disk
    private readonly Dictionary<string, string> _languages = new(StringComparer.OrdinalIgnoreCase);

    //Pending syntax checks of javascript buffers, debounced per uri
    private readonly Dictionary<string, CancellationTokenSource> _scriptChecks = new(StringComparer.OrdinalIgnoreCase);

    //Projects the session has opened, either on access or all of them when the setting asks for it
    private readonly ProjectRegistry _projects = new();

    //C# completion reuses one compilation per project, which a save invalidates
    private readonly CSharpCompletion _csharp;

    private readonly NcModChecker _checker;

    //Runs the editor's ncm commands, so the process and the interrupt that ends it live in this process
    private readonly NcmRunner _runner;

    //Workspace folder the editor opened, which is where ncm has to run
    private string _root = Environment.CurrentDirectory;

    //Whether the project is checked without being asked, which the editor setting can turn off
    private bool _autoCheck = true;

    //Whether a completion request may build the project compilation, which is off unless the user opts in
    private bool _completionBuild;

    //Whether every project under the workspace is registered at startup instead of on access
    private bool _projectLoadAll;

    //Checks run one at a time, so a burst of saves cannot start several compilations
    private readonly SemaphoreSlim _checks = new(1, 1);

    //Cancelled when the editor asks the server to stop, so a run in flight does not delay the exit
    private readonly CancellationTokenSource _stop = new();

    //The run the latest request started, cancelled when a newer one arrives
    private readonly object _gate = new();

    private CancellationTokenSource? _pending;

    //Files the last check published problems for, per project, so a fixed file can be cleared
    private readonly Dictionary<string, HashSet<string>> _published = new(StringComparer.OrdinalIgnoreCase);

    public LspServer(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
        _checker = new NcModChecker(Log);
        _runner = new NcmRunner(Log, SendProgress, SendFinished, SendState);
        _csharp = new CSharpCompletion(_projects);
    }

    //ApplySettings turns the editor switches into the store's update mode and the check schedule
    //The environment switch of a release pipeline wins, since there the download has to stay off whatever the editor says
    private void ApplySettings(JsonObject? settings)
    {
        if (settings?["autoUpdateKernel"] is JsonValue kernel && kernel.TryGetValue<bool>(out var update))
            ServerStore.SkipUpdate = ServerStore.SkipUpdateFromEnvironment || !update;

        //a completion must not start a full build unless the user asked for it, since that would stall a large project
        if (settings?["completionBuild"] is JsonValue completion && completion.TryGetValue<bool>(out var build))
            _completionBuild = build;

        //loading all projects up front is a choice, since a workspace may hold several unrelated mods
        if (settings?["projectLoad"] is JsonValue projects && projects.TryGetValue<string>(out var mode))
        {
            _projectLoadAll = string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase);
            if (_projectLoadAll)
                _projects.LoadAll(_root);
        }

        if (settings?["autoCheck"] is not JsonValue check || !check.TryGetValue<bool>(out var auto))
            return;

        //turning the checks back on is what brings the page up to date again
        var enabled = auto && !_autoCheck;
        _autoCheck = auto;

        if (enabled)
            PublishManifest();
    }

    //CaptureRoot takes the folder the editor opened, which is where every ncm command runs
    //The editor may hand over a uri, a uri style path with a leading slash, or no root at all
    private void CaptureRoot(JsonObject? parameters)
    {
        var value = parameters?["rootUri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(value) && parameters?["workspaceFolders"] is JsonArray folders && folders.Count > 0)
            value = folders[0]?["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(value))
            value = parameters?["rootPath"]?.GetValue<string>();
        if (string.IsNullOrEmpty(value))
            return;

        var path = value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.IsFile)
            path = parsed.LocalPath;

        path = Uri.UnescapeDataString(path);

        //a windows path written the uri way keeps the slash before the drive, which is not a directory of its own
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
            path = path[1..];

        _root = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }

    //Asm serves one request from the assembly page
    //The page can point at any dll, so nothing here is limited to the project; resource edits rewrite that file
    private static JsonObject Asm(JsonObject? parameters)
    {
        var file = parameters?["file"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(file))
            return Failed("no assembly given");

        var path = Path.GetFullPath(file);
        if (!File.Exists(path))
            return Failed($"no such assembly {path}");

        var mode = parameters?["mode"]?.GetValue<string>() ?? "summary";
        var query = parameters?["query"]?.GetValue<string>() ?? string.Empty;

        try
        {
            switch (mode)
            {
                case "summary":
                {
                    var summary = AsmBridge.Summary(path);
                    var types = new JsonArray();
                    foreach (var type in summary.Types)
                    {
                        types.Add(new JsonObject
                        {
                            ["kind"] = type.Kind,
                            ["name"] = type.Name,
                        });
                    }

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["name"] = summary.Name,
                        ["version"] = summary.Version,
                        ["methods"] = summary.Methods,
                        ["types"] = types,
                    };
                }

                case "type":
                    return Text(AsmBridge.Details(path, query));

                case "decompile":
                    return Text(AsmBridge.Decompile(path, query));

                case "il":
                    return Text(AsmBridge.Il(path, query));

                case "dependencies":
                    return Text(AsmBridge.Dependencies(path));

                case "resources":
                {
                    var rows = new JsonArray();
                    foreach (var resource in AsmBridge.Resources(path))
                    {
                        rows.Add(new JsonObject
                        {
                            ["name"] = resource.Name,
                            ["size"] = resource.Size,
                            ["kind"] = resource.Kind,
                        });
                    }

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["resources"] = rows,
                    };
                }

                case "extract":
                {
                    var bytes = AsmBridge.ReadResource(path, query);
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["name"] = Path.GetFileName(query),
                        ["data"] = Convert.ToBase64String(bytes),
                    };
                }

                case "embed":
                    return Text(string.Join(Environment.NewLine, AsmBridge.Embed(path, Items(parameters))));

                case "remove":
                    return Text(string.Join(Environment.NewLine, AsmBridge.Remove(path, Items(parameters))));

                default:
                    return Failed($"unknown mode {mode}");
            }
        }
        catch (Exception e)
        {
            return Failed(e.Message);
        }
    }

    //References what the project references: the kernel assemblies on disk, the mod api switch and the packages
    private JsonObject References()
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var kernel = new JsonArray();
        var kernelDirectory = Path.Combine(project.Directory, ProjectLayout.Kernel);
        if (Directory.Exists(kernelDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(kernelDirectory, "*.dll")
                         .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            {
                kernel.Add(Path.GetFileName(file) ?? string.Empty);
            }
        }

        var packages = new JsonArray();
        foreach (var package in project.Packages)
        {
            packages.Add(new JsonObject
            {
                ["id"] = package.Id,
                ["version"] = package.Version,
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["config"] = project.Path,
            ["kernel"] = kernel,
            ["modApi"] = project.Build.DependsOnModApi,
            ["packages"] = packages,
        };
    }

    //ReferenceSet installs or removes one reference and answers with the list that results from it
    private JsonObject ReferenceSet(JsonObject? parameters)
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var kind = parameters?["kind"]?.GetValue<string>() ?? "package";
        var remove = parameters?["remove"]?.GetValue<bool>() ?? false;
        var id = parameters?["id"]?.GetValue<string>() ?? string.Empty;
        var version = parameters?["version"]?.GetValue<string>() ?? string.Empty;

        var changed = kind switch
        {
            "modApi" => project.SetDependsOnModApi(!remove, out error),
            _ when remove => project.RemovePackage(id, out error),
            _ => project.AddPackage(id, version, out error),
        };

        return changed ? References() : Failed(error);
    }

    //Config lists every setting the config may declare, absent ones carrying the default the build assumes
    private JsonObject Config()
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var entries = new JsonArray();
        foreach (var value in NcConfig.Read(project.Path))
        {
            var options = new JsonArray();
            foreach (var option in value.Field.Options)
                options.Add(option);

            entries.Add(new JsonObject
            {
                ["key"] = value.Field.Key,
                ["group"] = value.Field.Element,
                ["label"] = value.Field.Attribute,
                ["kind"] = value.Field.Kind,
                ["options"] = options,
                ["default"] = value.Field.Default,
                ["value"] = value.Value,
                ["present"] = value.Present,
                ["required"] = value.Field.Required,
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["config"] = project.Path,
            ["entries"] = entries,
        };
    }

    //ConfigSet writes or removes one setting, answering with the list that results from it
    private JsonObject ConfigSet(JsonObject? parameters)
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var key = parameters?["key"]?.GetValue<string>() ?? string.Empty;
        var remove = parameters?["remove"]?.GetValue<bool>() ?? false;
        var value = parameters?["value"]?.GetValue<string>() ?? string.Empty;

        var changed = remove
            ? NcConfig.Remove(project.Path, key, out error)
            : NcConfig.Set(project.Path, key, value, out error);

        return changed ? Config() : Failed(error);
    }

    //Tasks lists what the project declares, the override default and the task block for the page editor
    private JsonObject Tasks()
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        return TasksOf(project);
    }

    //TasksOf shapes one project's tasks, including the ones a built-in tool shadows
    private static JsonObject TasksOf(NcProject project)
    {
        var tasks = new JsonArray();
        AddTasks(tasks, project, project.Tasks, NcTasks.Fragments(project.Path), string.Empty);

        //a bootstrap exposes the tasks of its projects as <bootstrap>/<task>
        if (project.Bootstrap)
        {
            foreach (var child in project.ReferencedConfigs())
            {
                var fragments = NcTasks.Fragments(child.Path);
                AddTasks(tasks, child, child.Runnable(), fragments, project.ToolPrefix + "/");
            }
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["config"] = project.Path,
            ["overrides"] = project.Overrides,
            ["tasks"] = tasks,
        };
    }

    //AddTasks appends one project's tasks, with a prefix and its own config path when a bootstrap aggregates them
    private static void AddTasks(JsonArray tasks, NcProject owner, IReadOnlyList<NcTask> source,
        Dictionary<string, string> fragments, string prefix)
    {
        foreach (var task in source)
        {
            var depends = new JsonArray();
            foreach (var name in task.Depends)
                depends.Add(name);

            var steps = new JsonArray();
            foreach (var step in task.Steps)
                steps.Add(step.Text);

            tasks.Add(new JsonObject
            {
                ["name"] = prefix + task.Name,
                ["description"] = task.Description,
                ["depends"] = depends,
                ["overrides"] = task.Overrides,
                ["shadowed"] = owner.IsShadowed(task),
                ["steps"] = steps,
                ["xml"] = fragments.TryGetValue(task.Name, out var xml) ? xml : string.Empty,
                ["config"] = owner.Path,
            });
        }
    }

    //Projects lists the roots the session has registered, in the order they were opened
    private JsonObject Projects()
    {
        var items = new JsonArray();
        foreach (var root in _projects.Loaded)
            items.Add(root);

        return new JsonObject
        {
            ["ok"] = true,
            ["loaded"] = items,
        };
    }

    //TasksSaveTask replaces one task, or appends it when the given name is empty
    private JsonObject TasksSaveTask(JsonObject? parameters)
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var config = TaskConfig(project, parameters);
        if (config is null)
            return Failed("the task config is not part of this project");

        var name = TaskName(parameters?["name"]?.GetValue<string>() ?? string.Empty, config, project);
        var xml = parameters?["xml"]?.GetValue<string>() ?? string.Empty;
        return NcTasks.SaveTask(config, name, xml, out error) ? Tasks() : Failed(error);
    }

    //TasksRemove drops one task and answers with what remains
    private JsonObject TasksRemove(JsonObject? parameters)
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var config = TaskConfig(project, parameters);
        if (config is null)
            return Failed("the task config is not part of this project");

        var name = TaskName(parameters?["name"]?.GetValue<string>() ?? string.Empty, config, project);
        return NcTasks.RemoveTask(config, name, out error) ? Tasks() : Failed(error);
    }

    //TaskConfig validates that an edit targets the root config or one of the projects a bootstrap aggregates
    private static string? TaskConfig(NcProject project, JsonObject? parameters)
    {
        var config = parameters?["config"]?.GetValue<string>() ?? string.Empty;
        if (config.Length == 0 || string.Equals(config, project.Path, StringComparison.OrdinalIgnoreCase))
            return project.Path;

        return project.ReferencedConfigs().Any(child =>
            string.Equals(child.Path, config, StringComparison.OrdinalIgnoreCase))
            ? config
            : null;
    }

    //TaskName removes the bootstrap prefix when an edit targets an aggregated project
    private static string TaskName(string name, string config, NcProject project)
    {
        if (string.Equals(config, project.Path, StringComparison.OrdinalIgnoreCase))
            return name;

        var slash = name.LastIndexOf('/');
        return slash >= 0 && slash < name.Length - 1 ? name[(slash + 1)..] : name;
    }

    //TasksOverride writes the default that lets a task replace a built-in tool
    private JsonObject TasksOverride(JsonObject? parameters)
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var value = parameters?["value"]?.GetValue<bool>() ?? false;
        return NcTasks.SetOverride(project.Path, value, out error) ? Tasks() : Failed(error);
    }

    //TaskRun starts one project task through ncm, which keeps the process and its output in the tool
    //A task replacing a built-in tool is confirmed by the page first, since the tool cannot ask from a pipe
    private JsonObject TaskRun(JsonObject? parameters)
    {
        LoadPageProject();
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;

        //a bootstrap names a child task as <bootstrap>/<task>; approval and the run belong to the child
        var scoped = project.FindScopedTask(name);
        var owner = scoped?.Project ?? project;
        var task = scoped?.Task ?? project.FindTask(name);
        if (task is null)
            return Failed($"task {name} is not runnable, it is missing or shadowed by a built-in tool");

        if (task.Overrides && NcProject.IsBuiltInName(task.Name) && !TaskApprovals.IsApproved(owner, task))
        {
            if (parameters?["approve"]?.GetValue<bool>() != true)
            {
                var steps = new JsonArray();
                foreach (var step in task.Steps)
                    steps.Add(step.Text);

                return new JsonObject
                {
                    ["ok"] = false,
                    ["needsApproval"] = true,
                    ["name"] = name,
                    ["steps"] = steps,
                };
            }

            TaskApprovals.Approve(owner, task);
        }

        return new JsonObject { ["ok"] = _runner.StartTask(project.Directory, name) };
    }

    //ScriptComplete builds the environment a task run gets, so the editor completes the real members
    private JsonObject ScriptComplete(JsonObject? parameters)
    {
        var project = NcProject.TryFind(_root, out var error);
        if (project is null)
            return Failed(error.Length > 0 ? error : "no project config in this workspace");

        var source = parameters?["source"]?.GetValue<string>() ?? string.Empty;
        var line = parameters?["line"]?.GetValue<int>() ?? 1;
        var column = parameters?["column"]?.GetValue<int>() ?? 1;

        try
        {
            using var engine = ScriptApi.Create(project, TaskRunner.Variables(project, project.Directory),
                project.Directory, project.AllowScriptClr);

            var items = new JsonArray();
            foreach (var item in ScriptApi.Complete(engine, source, line, column))
            {
                items.Add(new JsonObject
                {
                    ["name"] = item.name,
                    ["kind"] = item.kind,
                });
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["items"] = items,
            };
        }
        catch (Exception e)
        {
            return Failed(e.Message);
        }
    }

    //ScriptCheck parses the script and answers with the first syntax error, so the page can mark the line
    private static JsonObject ScriptCheck(JsonObject? parameters)
    {
        var source = parameters?["source"]?.GetValue<string>() ?? string.Empty;
        var diagnostic = ScriptApi.Check(source);
        if (diagnostic is null)
            return new JsonObject { ["ok"] = true };

        return new JsonObject
        {
            ["ok"] = true,
            ["diagnostic"] = new JsonObject
            {
                ["line"] = diagnostic.line,
                ["column"] = diagnostic.column,
                ["message"] = diagnostic.message,
            },
        };
    }

    //Completion answers the editor's own completion request from the tracked buffer
    //Javascript uses the jint environment, C# uses the compilation the module check builds
    private JsonObject? Completion(JsonObject? parameters)
    {
        var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>() ?? string.Empty;
        if (!_documents.TryGetValue(uri, out var text))
            return null;

        var line = parameters?["position"]?["line"]?.GetValue<int>() ?? 0;
        var character = parameters?["position"]?["character"]?.GetValue<int>() ?? 0;

        if (IsScript(uri))
            return ScriptCompletion(uri, text, line + 1, character + 1);

        if (IsCSharp(uri))
        {
            var path = PathOfUri(uri);
            return path is null ? null : _csharp.Complete(path, text, line, character, _completionBuild);
        }

        return null;
    }

    //ScriptCompletion lists the members of the script runtime at a one based position
    private JsonObject? ScriptCompletion(string uri, string text, int line, int column)
    {
        var root = ScriptRootOf(uri);
        if (root is null)
            return null;

        try
        {
            var project = NcProject.TryFind(root, out _);
            if (project is null)
                return null;

            using var engine = ScriptApi.Create(project, TaskRunner.Variables(project, root),
                root, project.AllowScriptClr);

            var items = new JsonArray();
            foreach (var item in ScriptApi.Complete(engine, text, line, column))
            {
                items.Add(new JsonObject
                {
                    ["label"] = item.name,
                    ["kind"] = KindOf(item.kind),
                });
            }

            return new JsonObject
            {
                ["isIncomplete"] = false,
                ["items"] = items,
            };
        }
        catch
        {
            return null;
        }
    }

    //KindOf maps the kinds the script api reports to the numbers the protocol uses
    private static int KindOf(string kind) => kind switch
    {
        "method" => 2,
        "object" => 10,
        "keyword" => 14,
        _ => 12,
    };

    //IsScript tells whether a document is a javascript task script, using the language the editor sent
    private bool IsScript(string uri)
        => (_languages.TryGetValue(uri, out var language)
            && (language.Equals("javascript", StringComparison.OrdinalIgnoreCase)
                || language.Equals("javascriptreact", StringComparison.OrdinalIgnoreCase)))
           || uri.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
           || uri.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
           || uri.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase);

    //IsCSharp tells whether a document belongs to the project compilation
    private bool IsCSharp(string uri)
        => (_languages.TryGetValue(uri, out var language)
            && language.Equals("csharp", StringComparison.OrdinalIgnoreCase))
           || uri.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    //ScheduleScriptCheck parses a buffer once typing pauses, replacing the previous run for the same uri
    private void ScheduleScriptCheck(string uri)
    {
        if (!_documents.TryGetValue(uri, out var text))
            return;

        CancelScriptCheck(uri);
        var pending = new CancellationTokenSource();
        _scriptChecks[uri] = pending;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, pending.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (pending.IsCancellationRequested)
                return;

            var diagnostic = ScriptApi.Check(text);
            var items = new JsonArray();
            if (diagnostic is not null)
            {
                items.Add(new JsonObject
                {
                    //the script api reports one based positions
                    ["range"] = new JsonObject
                    {
                        ["start"] = new JsonObject
                        {
                            ["line"] = Math.Max(diagnostic.line - 1, 0),
                            ["character"] = Math.Max(diagnostic.column - 1, 0),
                        },
                        ["end"] = new JsonObject
                        {
                            ["line"] = Math.Max(diagnostic.line - 1, 0),
                            ["character"] = Math.Max(diagnostic.column, 0),
                        },
                    },
                    ["severity"] = 1,
                    ["source"] = "nacrecraft",
                    ["message"] = diagnostic.message,
                });
            }

            SendDiagnosticItems(uri, items);
        });
    }

    private void CancelScriptCheck(string uri)
    {
        if (_scriptChecks.Remove(uri, out var pending))
            pending.Cancel();
    }

    //ScriptRootOf finds the project holding a script document, walking up from the file
    private static string? ScriptRootOf(string uri)
        => PathOfUri(uri) is { } path ? ProjectRootOfPath(path) : null;

    //ProjectRootOfPath finds the ncproj directory that owns a file or folder
    private static string? ProjectRootOfPath(string path)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory);

        if (string.IsNullOrEmpty(directory))
            return null;

        try
        {
            var config = NcProject.TryFind(directory, out _);
            return config is null ? null : Path.GetDirectoryName(config.Path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    //LoadProjectOf registers the project of an opened document, which is the on access half of the load policy
    private void LoadProjectOf(string uri)
    {
        var path = PathOfUri(uri);
        if (path is null)
            return;

        var root = ProjectRootOfPath(path);
        if (root is not null)
            _projects.Load(root);
    }

    //LoadPageProject registers the project a page request touches, even when the request only reads the config
    private void LoadPageProject()
    {
        var root = ProjectRootOfPath(_root);
        if (root is not null)
            _projects.Load(root);
    }

    //PathOfUri decodes a file uri the way the editor writes it, including the drive colon
    private static string? PathOfUri(string uri)
    {
        try
        {
            var path = Uri.UnescapeDataString(new Uri(uri).AbsolutePath);
            if (path.Length >= 3 && path[0] == '/' && path[2] == ':')
                path = path[1..];
            return path;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    //Search endpoints already resolved from a feed index, so repeated searches do not ask for it again
    private static readonly Dictionary<string, string> SearchEndpoints = new(StringComparer.OrdinalIgnoreCase);

    //NuGetSearch asks a v3 feed for packages, resolving the search endpoint from its index first
    private static JsonObject NuGetSearch(JsonObject? parameters)
    {
        var query = parameters?["query"]?.GetValue<string>() ?? string.Empty;
        var source = parameters?["source"]?.GetValue<string>() ?? "https://api.nuget.org/v3/index.json";

        try
        {
            var endpoint = SearchEndpoint(source);
            var url = $"{endpoint}?q={Uri.EscapeDataString(query)}&take=30&prerelease=false&semVerLevel=2.0.0";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var data = JsonNode.Parse(client.GetStringAsync(url).GetAwaiter().GetResult())?["data"] as JsonArray;

            var packages = new JsonArray();
            if (data is not null)
            {
                foreach (var entry in data)
                {
                    if (entry is not JsonObject item)
                        continue;

                    packages.Add(new JsonObject
                    {
                        ["id"] = item["id"]?.GetValue<string>() ?? string.Empty,
                        ["version"] = item["version"]?.GetValue<string>() ?? string.Empty,
                        ["description"] = item["description"]?.GetValue<string>() ?? string.Empty,
                        ["icon"] = item["iconUrl"]?.GetValue<string>() ?? string.Empty,
                        ["downloads"] = item["totalDownloads"]?.GetValue<long>() ?? 0,
                    });
                }
            }

            return new JsonObject
            {
                ["ok"] = true,
                ["packages"] = packages,
            };
        }
        catch (Exception e)
        {
            return Failed(e.Message);
        }
    }

    //SearchEndpoint finds the SearchQueryService address of a feed, or returns the source when it already is one
    private static string SearchEndpoint(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"{source} is not an http address");

        if (uri.AbsolutePath.Contains("/query", StringComparison.OrdinalIgnoreCase))
            return source;

        lock (SearchEndpoints)
        {
            if (SearchEndpoints.TryGetValue(source, out var known))
                return known;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resources = JsonNode.Parse(client.GetStringAsync(source).GetAwaiter().GetResult())?["resources"] as JsonArray;
        if (resources is not null)
        {
            foreach (var resource in resources)
            {
                var type = resource?["@type"]?.GetValue<string>() ?? string.Empty;
                if (!type.StartsWith("SearchQueryService", StringComparison.OrdinalIgnoreCase))
                    continue;

                var address = resource?["@id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(address))
                    continue;

                lock (SearchEndpoints)
                    SearchEndpoints[source] = address;

                return address;
            }
        }

        throw new InvalidOperationException($"{source} does not offer a search service");
    }

    //Readmes already downloaded, keyed by package and version
    private static readonly Dictionary<string, string> Readmes = new(StringComparer.OrdinalIgnoreCase);

    //NuGetReadme downloads a package and answers with the markdown document it carries
    //The readme only lives inside the package file, the search answer does not include it
    private static JsonObject NuGetReadme(JsonObject? parameters)
    {
        var id = parameters?["id"]?.GetValue<string>() ?? string.Empty;
        var version = parameters?["version"]?.GetValue<string>() ?? string.Empty;
        var source = parameters?["source"]?.GetValue<string>() ?? "https://api.nuget.org/v3/index.json";

        if (id.Length == 0 || version.Length == 0)
            return Failed("package id and version are required");

        var key = $"{id}/{version}";
        lock (Readmes)
        {
            if (Readmes.TryGetValue(key, out var known))
                return new JsonObject { ["ok"] = true, ["text"] = known };
        }

        try
        {
            var lower = id.ToLowerInvariant();
            var lowerVersion = version.ToLowerInvariant();
            var url = $"{PackageBaseAddress(source).TrimEnd('/')}/{lower}/{lowerVersion}/{lower}.{lowerVersion}.nupkg";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            //a huge package is not worth a document, the description already answers the pane
            if (response.Content.Headers.ContentLength is > 64 * 1024 * 1024)
                return new JsonObject { ["ok"] = true, ["text"] = string.Empty };

            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            //the readme sits at the archive root when the author followed the convention
            //a license file is markdown too, so the readme named entries win before the rest
            var entry = archive.Entries
                .Where(item => item.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.Name.Equals("readme.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(item => item.FullName.Count(c => c == '/'))
                .ThenBy(item => item.FullName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (entry is null)
            {
                lock (Readmes)
                    Readmes[key] = string.Empty;

                return new JsonObject { ["ok"] = true, ["text"] = string.Empty };
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();

            lock (Readmes)
                Readmes[key] = text;

            return new JsonObject { ["ok"] = true, ["text"] = text };
        }
        catch (Exception e)
        {
            return Failed(e.Message);
        }
    }

    //Version lists already read from a feed, keyed by source and package
    private static readonly Dictionary<string, JsonArray> VersionLists = new(StringComparer.OrdinalIgnoreCase);

    //NuGetVersions answers every version a package published, newest first
    private static JsonObject NuGetVersions(JsonObject? parameters)
    {
        var id = parameters?["id"]?.GetValue<string>() ?? string.Empty;
        var source = parameters?["source"]?.GetValue<string>() ?? "https://api.nuget.org/v3/index.json";

        if (id.Length == 0)
            return Failed("package id is required");

        var key = $"{source}\n{id}";
        lock (VersionLists)
        {
            if (VersionLists.TryGetValue(key, out var known))
                return new JsonObject { ["ok"] = true, ["versions"] = known.DeepClone() };
        }

        try
        {
            var lower = id.ToLowerInvariant();
            var url = $"{PackageBaseAddress(source).TrimEnd('/')}/{lower}/index.json";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var data = JsonNode.Parse(client.GetStringAsync(url).GetAwaiter().GetResult())?["versions"] as JsonArray;

            var versions = new JsonArray();
            if (data is not null)
            {
                //the feed lists them oldest first, while the picker wants the newest on top
                foreach (var node in data.Reverse())
                {
                    var version = node?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(version))
                        versions.Add(version);
                }
            }

            lock (VersionLists)
                VersionLists[key] = versions;

            return new JsonObject { ["ok"] = true, ["versions"] = versions.DeepClone() };
        }
        catch (Exception e)
        {
            return Failed(e.Message);
        }
    }

    //Package bases already resolved from a feed index, which is where the package files are downloaded from
    private static readonly Dictionary<string, string> PackageBases = new(StringComparer.OrdinalIgnoreCase);

    //PackageBaseAddress finds the PackageBaseAddress resource of a feed, or returns the source when it already is one
    private static string PackageBaseAddress(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"{source} is not an http address");

        if (uri.AbsolutePath.Contains("/flatcontainer", StringComparison.OrdinalIgnoreCase))
            return source;

        lock (PackageBases)
        {
            if (PackageBases.TryGetValue(source, out var known))
                return known;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resources = JsonNode.Parse(client.GetStringAsync(source).GetAwaiter().GetResult())?["resources"] as JsonArray;
        if (resources is not null)
        {
            foreach (var resource in resources)
            {
                var type = resource?["@type"]?.GetValue<string>() ?? string.Empty;
                if (!type.StartsWith("PackageBaseAddress", StringComparison.OrdinalIgnoreCase))
                    continue;

                var address = resource?["@id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(address))
                    continue;

                lock (PackageBases)
                    PackageBases[source] = address;

                return address;
            }
        }

        throw new InvalidOperationException($"{source} does not offer a package base address");
    }

    //Items reads the string array the resource operations carry
    private static List<string> Items(JsonObject? parameters)
    {
        var items = new List<string>();
        if (parameters?["items"] is not JsonArray array)
            return items;

        foreach (var node in array)
        {
            var value = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
                items.Add(value);
        }

        return items;
    }

    private static JsonObject Text(string text) => new() { ["ok"] = true, ["text"] = text };

    private static JsonObject Failed(string message) => new() { ["ok"] = false, ["error"] = message };

    //SendProgress reports one line of tool output, which the editor shows inside its progress notification
    private void SendProgress(string action, string message)
        => Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "nacrecraft/progress",
            ["params"] = new JsonObject
            {
                ["action"] = action,
                ["message"] = message,
            },
        });

    //SendFinished reports how a command ended, with the tail of its output for the failure dialog
    private void SendFinished(string action, bool ok, int code, string output)
        => Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "nacrecraft/finished",
            ["params"] = new JsonObject
            {
                ["action"] = action,
                ["ok"] = ok,
                ["code"] = code,
                ["output"] = output,
            },
        });

    //SendState tells the editor which launches are running, which is what flips the side bar buttons
    private void SendState(IReadOnlyList<string> running)
    {
        var items = new JsonArray();
        foreach (var action in running)
            items.Add(action);

        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "nacrecraft/state",
            ["params"] = new JsonObject
            {
                ["running"] = items,
            },
        });
    }

    //Log reports a message through the standard log notification, which the editor shows in the page's log panel
    private void Log(int type, string message)
        => Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "window/logMessage",
            ["params"] = new JsonObject
            {
                ["type"] = type,
                ["message"] = message,
            },
        });

    //LogLine forwards one line of tool output to the editor log
    internal void LogLine(string line)
    {
        if (line.Length > 0)
            Log(3, line);
    }

    //Run reads messages until the stream ends or the editor says exit
    public void Run()
    {
        while (true)
        {
            var body = ReadMessage();
            if (body is null)
                return;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                continue;
            }

            if (message is not JsonObject request)
                continue;

            if (!Handle(request))
                return;
        }
    }

    //Handle one message, returning false when the process should stop
    private bool Handle(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();
        //a response to a request this server never sends is not interesting
        if (method is null)
            return true;

        var id = message["id"];
        var parameters = message["params"] as JsonObject;

        switch (method)
        {
            case "initialize":
                CaptureRoot(parameters);
                ApplySettings(parameters?["initializationOptions"] as JsonObject);
                Respond(id, new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        //push diagnostics, full document sync on save
                        ["textDocumentSync"] = new JsonObject
                        {
                            ["openClose"] = true,
                            ["change"] = 1,
                            ["save"] = true,
                        },
                        //javascript buffers complete from the same project model the task page uses
                        ["completionProvider"] = new JsonObject
                        {
                            ["triggerCharacters"] = new JsonArray("."),
                            ["resolveProvider"] = false,
                        },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "NacreCraft NcMod LangServer",
                        ["version"] = "0.1.0",
                    },
                });
                return true;

            case "initialized":
                //the project is checked once the session is up, so opening the folder is enough to get problems
                if (_projectLoadAll)
                    _projects.LoadAll(_root);
                PublishManifest();
                return true;

            //a source or manifest changed outside the editor, which the client watches for when that is on
            case "workspace/didChangeWatchedFiles":
                _csharp.Clear();
                PublishManifest();
                return true;

            //the editor changed a setting, which for now is only whether the kernel may be fetched
            case "workspace/didChangeConfiguration":
                ApplySettings(parameters?["settings"] as JsonObject);
                return true;

            //the side bar buttons end up here, the editor itself never starts a process
            case "nacrecraft/run":
            {
                var action = parameters?["action"]?.GetValue<string>() ?? string.Empty;
                Respond(id, JsonValue.Create(_runner.Start(_root, action)));
                return true;
            }

            case "nacrecraft/stop":
            {
                var action = parameters?["action"]?.GetValue<string>() ?? string.Empty;
                Respond(id, JsonValue.Create(_runner.Stop(action)));
                return true;
            }

            //the assembly page inspects any dll through the asm code, so no process is started per request
            case "nacrecraft/asm":
                Respond(id, Asm(parameters));
                return true;

            //the package page lists what the project references and edits the ncproj for install and uninstall
            case "nacrecraft/references":
                Respond(id, References());
                return true;

            case "nacrecraft/referenceSet":
                Respond(id, ReferenceSet(parameters));
                return true;

            case "nacrecraft/config":
                Respond(id, Config());
                return true;

            case "nacrecraft/configSet":
                Respond(id, ConfigSet(parameters));
                return true;

            case "nacrecraft/tasks":
                Respond(id, Tasks());
                return true;

            case "nacrecraft/tasksSaveTask":
                Respond(id, TasksSaveTask(parameters));
                return true;

            case "nacrecraft/tasksRemove":
                Respond(id, TasksRemove(parameters));
                return true;

            case "nacrecraft/tasksOverride":
                Respond(id, TasksOverride(parameters));
                return true;

            case "nacrecraft/taskRun":
                Respond(id, TaskRun(parameters));
                return true;

            //the page can see which projects the session opened and ask for one explicitly
            case "nacrecraft/projects":
                Respond(id, Projects());
                return true;

            case "nacrecraft/projectLoad":
            {
                var root = parameters?["root"]?.GetValue<string>() ?? _root;
                _projects.Load(root);
                Respond(id, Projects());
                return true;
            }

            //the javascript editor asks what the real runtime exposes at the cursor
            case "nacrecraft/scriptComplete":
                Respond(id, ScriptComplete(parameters));
                return true;

            //the javascript editor asks whether the text parses before it saves the task
            case "nacrecraft/scriptCheck":
                Respond(id, ScriptCheck(parameters));
                return true;

            case "nacrecraft/nugetSearch":
                Respond(id, NuGetSearch(parameters));
                return true;

            case "nacrecraft/nugetReadme":
                Respond(id, NuGetReadme(parameters));
                return true;

            case "nacrecraft/nugetVersions":
                Respond(id, NuGetVersions(parameters));
                return true;

            case "textDocument/completion":
                Respond(id, Completion(parameters));
                return true;

            case "textDocument/didOpen":
            {
                var document = parameters?["textDocument"] as JsonObject;
                var uri = document?["uri"]?.GetValue<string>();
                var text = document?["text"]?.GetValue<string>();
                if (uri is not null && text is not null)
                {
                    _documents[uri] = text;
                    _languages[uri] = document?["languageId"]?.GetValue<string>() ?? string.Empty;
                    LoadProjectOf(uri);

                    //a script buffer is parsed on its own; only C# and manifests need the project check
                    if (IsScript(uri))
                        ScheduleScriptCheck(uri);
                    else
                        Publish(uri);
                }

                return true;
            }

            //the server declared full sync, so the buffer arrives as one replacement text
            case "textDocument/didChange":
            {
                var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
                var changes = parameters?["contentChanges"] as JsonArray;
                var text = changes is { Count: > 0 }
                    ? changes[^1]?["text"]?.GetValue<string>()
                    : null;

                if (uri is not null && text is not null)
                {
                    _documents[uri] = text;
                    LoadProjectOf(uri);
                    if (IsScript(uri))
                        ScheduleScriptCheck(uri);
                }

                return true;
            }

            case "textDocument/didSave":
            {
                var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
                if (uri is not null)
                {
                    if (IsScript(uri))
                        ScheduleScriptCheck(uri);
                    else
                    {
                        //the compilation behind completion no longer matches the saved sources
                        _csharp.Clear();
                        Publish(uri);
                    }
                }

                return true;
            }

            case "textDocument/didClose":
            {
                //the last result stays, a preview tab closes as soon as another file is opened and clearing would drop
                //the problems of a manifest nobody has to edit any more
                var uri = parameters?["textDocument"]?["uri"]?.GetValue<string>();
                if (uri is not null)
                {
                    _documents.Remove(uri);
                    var script = IsScript(uri);
                    _languages.Remove(uri);
                    CancelScriptCheck(uri);

                    //a closed script has no buffer left, so its problems go away with it
                    if (script)
                        SendDiagnosticItems(uri, new JsonArray());
                }

                return true;
            }

            case "shutdown":
                //cancel whatever is running, the editor is closing the session
                _stop.Cancel();
                _runner.StopAll();
                Respond(id, null);
                return true;

            case "exit":
                _stop.Cancel();
                return false;

            default:
                if (id is not null)
                    RespondError(id, -32601, $"method not found: {method}");
                return true;
        }
    }

    //Publish runs the inspection for the project holding one document
    //The check compiles the project, which can take long enough that doing it on the message loop would delay
    //shutdown, so it runs in the background and only the latest request survives
    private void Publish(string uri)
    {
        //automatic checks are a setting, and with it off the last result simply stays on screen
        if (!_autoCheck)
            return;
        string? path;
        try
        {
            //the absolute path keeps the percent encoded segments, which are decoded here to get a real file path
            path = Uri.UnescapeDataString(new Uri(uri).AbsolutePath);
        }
        catch (UriFormatException)
        {
            return;
        }

        //an encoded drive colon leaves a leading separator that makes the path invalid on windows
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':')
            path = path[1..];

        CancellationTokenSource pending;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            pending = _pending;
        }

        var token = pending.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _checks.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var result = _checker.Check(path, out var root);
                if (!token.IsCancellationRequested && root is not null)
                    PublishAll(root, result);
            }
            catch (Exception e)
            {
                //a check that fails must not end the session, the next save runs it again
                Log(1, $"check failed: {e.Message}");
            }
            finally
            {
                _checks.Release();
            }
        });
    }

    //PublishManifest checks the whole project, anchored at its manifest, which is what an editor start or a change
    //made outside the editor needs; a folder without a manifest has nothing to check
    private void PublishManifest()
    {
        var manifest = Path.Combine(_root, "ncmod.json");
        if (File.Exists(manifest))
            Publish(new Uri(manifest).AbsoluteUri);
    }

    //PublishAll sends the result grouped by file, since a check covers the whole project rather than the saved document
    //Files that had problems last time and have none now are cleared, which is what makes a fix disappear
    private void PublishAll(string root, IReadOnlyList<Diagnostic> diagnostics)
    {
        var groups = new Dictionary<string, List<Diagnostic>>(StringComparer.OrdinalIgnoreCase);
        foreach (var diagnostic in diagnostics)
        {
            var file = Path.GetFullPath(Path.Combine(root, diagnostic.File));
            if (!groups.TryGetValue(file, out var list))
                groups[file] = list = new List<Diagnostic>();
            list.Add(diagnostic);
        }

        var current = new HashSet<string>(groups.Keys, StringComparer.OrdinalIgnoreCase);
        if (_published.TryGetValue(root, out var previous))
        {
            foreach (var stale in previous.Where(file => !current.Contains(file)))
                SendDiagnostics(FileUri(stale), []);
        }

        _published[root] = current;

        foreach (var (file, list) in groups)
            SendDiagnostics(FileUri(file), list);
    }

    //FileUri turns an absolute path into the uri form the editor uses for its documents
    private static string FileUri(string path) => new Uri(path).AbsoluteUri;

    //SendDiagnostics forwards one result to the editor
    private void SendDiagnostics(string uri, IReadOnlyList<Diagnostic> diagnostics)
    {
        var items = new JsonArray();
        foreach (var diagnostic in diagnostics)
        {
            //the advice the command line prints under a diagnostic rides along in the message, which is what the editor
            //shows on hover and in the problems list; without it a missing using looks like a bare compiler error
            var message = new StringBuilder(diagnostic.Message);
            if (!string.IsNullOrEmpty(diagnostic.Note))
                message.Append('\n').Append(diagnostic.Note);
            foreach (var fix in diagnostic.Fixes)
                message.Append("\nhelp: ").Append(fix.Message);

            items.Add(new JsonObject
            {
                //lsp positions are zero based while the inspection reports one based ones
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject
                    {
                        ["line"] = Math.Max(diagnostic.Line - 1, 0),
                        ["character"] = Math.Max(diagnostic.Column - 1, 0),
                    },
                    ["end"] = new JsonObject
                    {
                        ["line"] = Math.Max(diagnostic.Line - 1, 0),
                        ["character"] = Math.Max(diagnostic.Column - 1 + Math.Max(diagnostic.Length, 1), 0),
                    },
                },
                ["severity"] = diagnostic.Severity == DiagnosticSeverity.Error ? 1 : 2,
                ["code"] = diagnostic.Code,
                ["source"] = "nacrecraft",
                ["message"] = message.ToString(),
            });
        }

        SendDiagnosticItems(uri, items);
    }

    //SendDiagnosticItems forwards one prepared diagnostic array to the editor
    private void SendDiagnosticItems(string uri, JsonArray items)
    {
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "textDocument/publishDiagnostics",
            ["params"] = new JsonObject
            {
                ["uri"] = uri,
                ["diagnostics"] = items,
            },
        });
    }

    private void Respond(JsonNode? id, JsonNode? result)
    {
        if (id is null)
            return;

        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result,
        });
    }

    private void RespondError(JsonNode id, int code, string message)
        => Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        });

    //ReadMessage reads one Content-Length framed message, returning null when the stream ends
    private string? ReadMessage()
    {
        var length = -1;
        while (true)
        {
            var line = ReadLine();
            if (line is null)
                return null;
            if (line.Length == 0)
                break;

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed))
                length = parsed;
        }

        if (length < 0)
            return null;

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var got = input.Read(body, read, length - read);
            if (got <= 0)
                return null;
            read += got;
        }

        return Encoding.UTF8.GetString(body);
    }

    //ReadLine reads one ascii header line without its terminator
    private string? ReadLine()
    {
        var bytes = new List<byte>();
        while (true)
        {
            var next = input.ReadByte();
            if (next < 0)
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (next == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
            if (next != '\r')
                bytes.Add((byte)next);
        }
    }

    //Send writes one framed message
    private void Send(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (output)
        {
            try
            {
                output.Write(header);
                output.Write(body);
                output.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                //the editor closed the pipe while a background check was still logging, there is nobody left to tell
            }
        }
    }
}

//LogWriter turns console output of the build tools into log notifications
//The tools print progress and notes, and stdout is the protocol stream, so the text has to be carried inside a message
internal sealed class LogWriter(LspServer server) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value != '\n')
        {
            _line.Append(value);
            return;
        }

        server.LogLine(_line.ToString().TrimEnd('\r'));
        _line.Clear();
    }
}

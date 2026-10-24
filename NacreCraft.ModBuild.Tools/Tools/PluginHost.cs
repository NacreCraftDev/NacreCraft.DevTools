using System.Reflection;
using System.Runtime.Loader;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Tools;

//PluginHost installs ncm plugins and calls their entry points
//a plugin is a class library: the name picks the folder under the tool cache, the method picks an entry point on the
//type carrying that name, and that entry point is a public static int taking string[]
//plugins run inside the ncm process, so they reach everything ncm reaches, there is no sandbox
internal static class PluginHost
{
    //Install copies every dll beside the given one into the tool cache under the dll's own name
    //the whole folder goes along so a plugin that ships its own dependencies still loads, and installing twice overwrites
    public static bool Install(string path, out string error)
    {
        error = string.Empty;
        var source = Path.GetFullPath(path);
        if (!File.Exists(source))
        {
            error = $"{path} does not exist";
            return false;
        }

        if (!string.Equals(Path.GetExtension(source), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{path} is not a dll";
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(source);
        var destination = Path.Combine(CacheLayout.Tool, name);
        Directory.CreateDirectory(destination);

        var copied = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(source)!, "*.dll"))
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                //Installing straight from the plugin folder would copy a file onto itself
                if (Path.GetFullPath(target).Equals(file, StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(file, target, overwrite: true);
                copied++;
            }
        }
        catch (IOException e)
        {
            error = $"cannot install {name}: {e.Message}";
            return false;
        }

        Console.WriteLine($"Installed plugin {name} at {destination} ({copied} dll(s))");
        Console.WriteLine($"Run it with 'ncm tool {name} <method>'");
        return true;
    }

    //Remove deletes one installed plugin
    public static bool Remove(string name, out string error)
    {
        error = string.Empty;
        var folder = Path.Combine(CacheLayout.Tool, name);
        if (!Directory.Exists(folder))
        {
            error = $"plugin {name} is not installed";
            Report();
            return false;
        }

        try
        {
            Directory.Delete(folder, recursive: true);
            Console.WriteLine($"Removed plugin {name}");
            return true;
        }
        catch (IOException e)
        {
            error = $"cannot remove {folder}: {e.Message}";
            return false;
        }
    }

    //Names the installed plugins, ordered so the listing stays stable
    public static List<string> Names()
    {
        if (!Directory.Exists(CacheLayout.Tool))
            return new List<string>();

        return Directory.EnumerateDirectories(CacheLayout.Tool)
            .Select(directory => Path.GetFileName(directory))
            .Where(name => !string.IsNullOrEmpty(name) && Location(name!) is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    //Location the plugin assembly, null when that plugin is not installed
    public static string? Location(string name)
    {
        var path = Path.Combine(CacheLayout.Tool, name, name + ".dll");
        return File.Exists(path) ? path : null;
    }

    //Run calls one entry point of an installed plugin and returns its exit code
    //an unknown plugin or method reports what is available instead of failing quietly
    public static int Run(string name, string method, string[] args, out string error)
    {
        error = string.Empty;
        var path = Location(name);
        if (path is null)
        {
            error = $"plugin {name} is not installed";
            Report();
            return 1;
        }

        var context = new PluginLoadContext(path);
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);

            var type = FindType(assembly, name, out error);
            if (type is null)
                return 1;

            var entry = FindEntry(type, method, out error);
            if (entry is null)
                return 1;

            try
            {
                var result = entry.Invoke(null, [args]);
                return result is int code ? code : 0;
            }
            catch (TargetInvocationException e)
            {
                error = $"plugin {name} threw in {method}: {e.InnerException?.Message ?? e.Message}";
                return 1;
            }
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            error = $"cannot load plugin {name}: {e.Message}";
            return 1;
        }
        finally
        {
            context.Unload();
        }
    }

    //FindType picks the class named after the plugin, which is where its entry points live
    //the name is compared against the simple and the full type name alike, so MyPlugin.Tools.dll can stand for the
    //class Tools in the namespace MyPlugin; a dll name may carry dots where a type name may not
    private static Type? FindType(Assembly assembly, string name, out string error)
    {
        error = string.Empty;
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            error = $"cannot read the types of plugin {name}: {e.LoaderExceptions.FirstOrDefault()?.Message ?? e.Message}";
            return null;
        }

        var matches = types.Where(type => Named(type, name)).ToList();
        if (matches.Count == 1)
            return matches[0];

        if (matches.Count > 1)
        {
            error = $"plugin {name} has more than one type matching that name: {string.Join(", ", matches.Select(type => type.FullName))}";
            return null;
        }

        //more often than not this is not a plugin at all, so the convention is spelled out and the nearby names shown
        error = $"plugin {name} declares no type named {name}, a plugin needs a public type carrying the dll name as its own name or as its full name{Suggested(types, name)}";
        return null;
    }

    //Named whether a type carries the plugin name, as its own name or as its full name
    private static bool Named(Type type, string name)
        => string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase);

    //Suggested the closest type names in the assembly, so a folder that holds no plugin is obvious at a glance
    private static string Suggested(Type[] types, string name)
    {
        var candidates = types
            .Select(type => type.FullName)
            .Where(full => !string.IsNullOrEmpty(full))
            .Select(full => full!)
            .ToList();

        var closest = Similarity.Rank(name, candidates, 4);
        return closest.Count == 0 ? string.Empty : $", the closest type names in it are {string.Join(", ", closest)}";
    }

    //FindEntry picks one entry point
    //when the name is missing the other usable ones are listed, that is what the caller needs to correct the call
    private static MethodInfo? FindEntry(Type type, string method, out string error)
    {
        error = string.Empty;
        var entries = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(IsEntry)
            .ToList();

        var found = entries.Where(item => string.Equals(item.Name, method, StringComparison.Ordinal)).ToList();
        if (found.Count == 1)
            return found[0];

        if (found.Count > 1)
            error = $"{type.FullName} has {found.Count} overloads named {method}, only 'public static int {method}(string[])' counts";
        else if (entries.Count == 0)
            error = $"{type.FullName} declares no entry point, one looks like 'public static int {method}(string[] args)'";
        else
            error = $"{type.FullName} has no entry named {method}, available ones are {string.Join(", ", entries.Select(item => item.Name))}";
        return null;
    }

    //IsEntry whether a method can serve as an entry point
    private static bool IsEntry(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return method.ReturnType == typeof(int)
            && parameters.Length == 1
            && parameters[0].ParameterType == typeof(string[]);
    }

    //Report lists what is installed, so a wrong name points at the right ones
    private static void Report()
    {
        var installed = Names();
        Console.WriteLine(installed.Count == 0
            ? "No plugin installed yet, add one with 'ncm tool add <dll path>'"
            : $"Installed plugins: {string.Join(", ", installed)}");
    }
}

//PluginLoadContext resolves a plugin's dependencies from its own folder
//whatever it does not carry falls back to the default context, so the BCL and the kernel assemblies still load
internal sealed class PluginLoadContext(string path) : AssemblyLoadContext(name: path, isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(path);

    protected override Assembly? Load(AssemblyName name)
    {
        var resolved = _resolver.ResolveAssemblyToPath(name);
        return resolved is null ? null : LoadFromAssemblyPath(resolved);
    }
}

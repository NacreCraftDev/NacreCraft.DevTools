using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace NacreCraft.NcMod.LangServer.Shim;

//Shim the tiny entry point one platform apphost starts
//It loads the real server two folders up, so one framework dependent copy serves every platform
public static class Shim
{
    public static int Main(string[] args)
    {
        //the apphost sits under server/apphost/<rid>, the server dll and its dependencies under server
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        var entry = Path.Combine(root, "NacreCraft.NcMod.LangServer.dll");
        if (!File.Exists(entry))
        {
            Console.Error.WriteLine($"language server not found at {entry}");
            return 1;
        }

        //The shim itself carries the server assembly name, since the apphost looks for a dll of that name
        //A separate context is what lets the real server with the same name live beside it
        var context = new AssemblyLoadContext("nacre-language-server", isCollectible: false);

        //the server folder holds every managed dependency, which this context cannot see from here
        context.Resolving += (loading, name) =>
        {
            if (name.Name is null)
                return null;

            var direct = Path.Combine(root, name.Name + ".dll");
            if (File.Exists(direct))
                return loading.LoadFromAssemblyPath(direct);

            //satellite assemblies live in their culture folder next to the server dll
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var localized = Path.Combine(directory, name.Name + ".dll");
                if (File.Exists(localized))
                    return loading.LoadFromAssemblyPath(localized);
            }

            return null;
        };

        //native libraries keep the runtimes/<rid>/native layout of a normal publish
        context.ResolvingUnmanagedDll += (_, name) =>
        {
            var native = Path.Combine(root, "runtimes", Rid(), "native");
            var candidate = Path.Combine(native, name);
            if (File.Exists(candidate))
                return NativeLibrary.Load(candidate);

            var plain = Path.Combine(root, name);
            return File.Exists(plain) ? NativeLibrary.Load(plain) : IntPtr.Zero;
        };

        var assembly = context.LoadFromAssemblyPath(entry);
        var main = assembly.EntryPoint;
        if (main is null)
        {
            Console.Error.WriteLine($"language server {entry} has no entry point");
            return 1;
        }

        return main.Invoke(null, new object?[] { args }) is int code ? code : 0;
    }

    //Rid the runtime identifier of the process, matching the folder layout of the packages
    private static string Rid()
    {
        var platform = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        return platform + "-" + architecture;
    }
}

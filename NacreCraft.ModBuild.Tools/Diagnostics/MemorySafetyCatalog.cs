using Microsoft.CodeAnalysis;

namespace NacreCraft.ModBuild.Diagnostics;

//MemorySafetyKind groups the flagged apis by what they hand back to the author, which is what the wording depends on
internal enum MemorySafetyKind
{
    //raw memory that nothing reclaims
    Allocation,
    //address arithmetic and the reads and writes that go with it
    Pointer,
    //an address or reference whose target the collector may still move or reclaim
    Alias,
    //space taken from the current stack frame
    Stack,
    //a call that leaves the runtime and enters native code
    Native,
}

//MemorySafetyCatalog lists the managed apis that put memory management back into the mod's hands
//The kernel loads every mod into the server process, so a wrong offset, a leaked buffer or a dangling handle does not
//stay inside the mod: it takes the whole server down with it
internal static class MemorySafetyCatalog
{
    //EveryMember marks a table entry that covers the whole type rather than one member
    private const string EveryMember = "*";

    //Members is the table, keyed by "type full name|member name"
    //only the members that actually touch memory are listed, which is why the table is spelled out instead of matching
    //whole namespaces: Marshal.SizeOf or Marshal.GetLastWin32Error cost nothing and reporting them would be noise
    private static readonly Dictionary<string, MemorySafetyKind> Members = new(StringComparer.Ordinal)
    {
        //whole types whose every member is an address operation
        ["System.Runtime.InteropServices.NativeMemory|" + EveryMember] = MemorySafetyKind.Allocation,
        ["System.Runtime.CompilerServices.Unsafe|" + EveryMember] = MemorySafetyKind.Alias,

        //the allocation side of Marshal
        ["System.Runtime.InteropServices.Marshal|AllocHGlobal"] = MemorySafetyKind.Allocation,
        ["System.Runtime.InteropServices.Marshal|FreeHGlobal"] = MemorySafetyKind.Allocation,
        ["System.Runtime.InteropServices.Marshal|ReAllocHGlobal"] = MemorySafetyKind.Allocation,
        ["System.Runtime.InteropServices.Marshal|AllocCoTaskMem"] = MemorySafetyKind.Allocation,
        ["System.Runtime.InteropServices.Marshal|FreeCoTaskMem"] = MemorySafetyKind.Allocation,
        ["System.Runtime.InteropServices.Marshal|ReAllocCoTaskMem"] = MemorySafetyKind.Allocation,

        //blocks and single values read or written through an address
        ["System.Runtime.InteropServices.Marshal|Copy"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|PtrToStructure"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|StructureToPtr"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|DestroyStructure"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|OffsetOf"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadByte"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadInt16"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadInt32"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadInt64"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadIntPtr"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadSByte"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadSingle"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReadDouble"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteByte"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteInt16"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteInt32"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteInt64"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteIntPtr"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteSByte"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteSingle"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|WriteDouble"] = MemorySafetyKind.Pointer,

        //strings crossing the boundary, which allocate a block the caller then owns
        ["System.Runtime.InteropServices.Marshal|StringToHGlobalAnsi"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|StringToHGlobalUni"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|StringToHGlobalAuto"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|StringToCoTaskMemAnsi"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|StringToCoTaskMemUni"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|StringToCoTaskMemAuto"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|PtrToStringAnsi"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|PtrToStringUni"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|PtrToStringAuto"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|PtrToStringUTF8"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|PtrToStringBSTR"] = MemorySafetyKind.Pointer,

        //function pointers and COM, both of which keep a native side alive behind a managed value
        ["System.Runtime.InteropServices.Marshal|GetFunctionPointerForDelegate"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|GetDelegateForFunctionPointer"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|GetIUnknownForObject"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|GetIDispatchForObject"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|GetObjectForIUnknown"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|GetTypedObjectForIUnknown"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|GetComInterfaceForObject"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|ReleaseComObject"] = MemorySafetyKind.Pointer,
        ["System.Runtime.InteropServices.Marshal|FinalReleaseComObject"] = MemorySafetyKind.Pointer,

        //pinning an object, after which the address only stays valid while the handle is held
        ["System.Runtime.InteropServices.GCHandle|Alloc"] = MemorySafetyKind.Alias,
        ["System.Runtime.InteropServices.GCHandle|Free"] = MemorySafetyKind.Alias,
        ["System.Runtime.InteropServices.GCHandle|AddrOfPinnedObject"] = MemorySafetyKind.Alias,
        ["System.Runtime.InteropServices.GCHandle|FromIntPtr"] = MemorySafetyKind.Alias,
        ["System.Runtime.InteropServices.GCHandle|ToIntPtr"] = MemorySafetyKind.Alias,

        //re-interpreting managed memory as a reference, which the interpreter then has no lifetime information for
        ["System.Runtime.InteropServices.MemoryMarshal|Cast"] = MemorySafetyKind.Alias,
        ["System.Runtime.InteropServices.MemoryMarshal|GetReference"] = MemorySafetyKind.Alias,
        ["System.Runtime.InteropServices.MemoryMarshal|GetArrayDataReference"] = MemorySafetyKind.Alias,
        ["System.Runtime.InteropServices.MemoryMarshal|CreateFromPinnedArray"] = MemorySafetyKind.Alias,

        //loading a native library, or calling into one that was loaded
        ["System.Runtime.InteropServices.NativeLibrary|Load"] = MemorySafetyKind.Native,
        ["System.Runtime.InteropServices.NativeLibrary|Free"] = MemorySafetyKind.Native,
        ["System.Runtime.InteropServices.NativeLibrary|GetExport"] = MemorySafetyKind.Native,
        ["System.Runtime.InteropServices.NativeLibrary|TryGetExport"] = MemorySafetyKind.Native,
        ["System.Runtime.InteropServices.NativeLibrary|TryLoad"] = MemorySafetyKind.Native,
    };

    //Text is the wording per kind: what the runtime will not do for you, and what to reach for instead
    private static readonly Dictionary<MemorySafetyKind, (string Problem, string Advice)> Text = new()
    {
        [MemorySafetyKind.Allocation] = (
            "allocates unmanaged memory that the collector neither tracks nor reclaims",
            "use a managed array, a pooled buffer, or a `Span<byte>` instead"),
        [MemorySafetyKind.Pointer] = (
            "reads or writes through a raw address, where a wrong offset or size corrupts the process without raising anything",
            "copy through `Span<T>` and managed buffers instead of address arithmetic"),
        [MemorySafetyKind.Alias] = (
            "hands out an address or reference the collector may still move or reclaim",
            "keep the handle inside the scope that owns the data, and never let the address outlive it"),
        [MemorySafetyKind.Stack] = (
            "takes space from the current stack frame, so a size that is not a small constant walks the frame towards the guard page",
            "keep the size a small compile-time constant, or allocate from the heap"),
        [MemorySafetyKind.Native] = (
            "leaves the runtime and enters native code, where a signature that does not match the native side corrupts the stack",
            "mirror the native declaration exactly, or use a package that already wraps it"),
    };

    //Kind returns what the called member is listed as, or null when it is not on the list at all
    public static MemorySafetyKind? Kind(IMethodSymbol method)
    {
        var type = method.ContainingType?.ToDisplayString();
        if (string.IsNullOrEmpty(type) || method.Name.Length == 0)
            return null;

        if (Members.TryGetValue($"{type}|{method.Name}", out var kind))
            return kind;

        return Members.TryGetValue($"{type}|{EveryMember}", out kind) ? kind : null;
    }

    //Describe turns a hit into the message and the advice, naming the api the way the source calls it
    public static (string Message, string Advice) Describe(string name, MemorySafetyKind kind)
    {
        var (problem, advice) = Text[kind];
        return ($"`{name}` {problem}", advice);
    }
}

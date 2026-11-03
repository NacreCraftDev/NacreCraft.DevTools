namespace NacreCraft.ModBuild.ToolsTest.Cases;

//the project does not enable unsafe code, so the advice names the csproj switch that does
//expect: add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to a `<PropertyGroup>` of NacreCraft.ModBuild.ToolsTest.csproj
public static unsafe class UnsafeCode
{
    public static int Size()
    {
        return sizeof(long);
    }
}

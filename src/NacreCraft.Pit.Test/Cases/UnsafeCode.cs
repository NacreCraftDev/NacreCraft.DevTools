namespace NacreCraft.Pit.Test.Cases;

//the project does not enable unsafe code, so the advice names the csproj switch that does
//expect: add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to a `<PropertyGroup>` of NacreCraft.Pit.Test.csproj
public static unsafe class UnsafeCode
{
    public static int Size()
    {
        return sizeof(long);
    }
}

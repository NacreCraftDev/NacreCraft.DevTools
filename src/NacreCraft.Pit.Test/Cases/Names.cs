namespace NacreCraft.Pit.Test.Cases;

//Shared types the cases refer to. Nothing here reports an error, so every
//diagnostic in the output belongs to a case file.
public sealed class Widget
{
    public int Count;

    public Widget(string first, string second)
    {
        First = first;
        Second = second;
    }

    public string First { get; }

    public string Second { get; }

    public void Run()
    {
    }

    public static void Reset()
    {
    }
}

public static class Tool
{
    public static void Run(int times)
    {
    }
}

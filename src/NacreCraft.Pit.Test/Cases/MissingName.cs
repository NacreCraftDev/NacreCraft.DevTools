namespace NacreCraft.Pit.Test.Cases;

public static class MissingName
{
    public static void Run()
    {
        //no namespace declares this name, so the nearby members are the only candidates
        //expect: did you mean `Prepared`?
        Preapred();
    }

    private static void Prepared()
    {
    }
}

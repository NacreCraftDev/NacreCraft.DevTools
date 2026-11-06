namespace NacreCraft.Pit.Test.Cases;

//These errors have no symbol level fix, so the static table supplies the general lead.
public static class GenericFallback
{
    public static void Conversion()
    {
        //expect: An implicit conversion from the source type to the target type does not exist
        int number = "text";
    }

    //expect: Every code path must return a value, add the missing return or throw
    public static int MissingReturn(bool flag)
    {
        if (flag)
        {
            return 1;
        }
    }

    //expect: Assign the local before its first read, or initialize it at declaration
    public static int Unassigned()
    {
        int value;
        return value;
    }
}

namespace NacreCraft.ModBuild.Core;

//ArgumentLine splits one configured argument line into command line arguments; double quotes group values with spaces and are stripped
internal static class ArgumentLine
{
    //Splits a line, an empty line yielding an empty array
    public static string[] Split(string line)
    {
        var parts = new List<string>();
        var builder = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;

        foreach (var character in line)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (started)
                {
                    parts.Add(builder.ToString());
                    builder.Clear();
                    started = false;
                }
                continue;
            }

            builder.Append(character);
            started = true;
        }

        //Whatever is still buffered is the final argument, accepted even if the quote never closed
        if (started)
            parts.Add(builder.ToString());

        return parts.ToArray();
    }
}

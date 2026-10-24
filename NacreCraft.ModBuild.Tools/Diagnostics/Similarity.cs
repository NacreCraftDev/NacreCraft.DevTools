namespace NacreCraft.ModBuild.Diagnostics;

//Similarity measures how close two names are, backing did-you-mean candidate selection
internal static class Similarity
{
    //Closest picks the nearest candidate, returning null when none is close enough instead of guessing
    //a candidate starting with the input wins first, since abbreviating a name is the common case
    //otherwise the smallest edit distance within bound wins, as anything farther would mislead
    public static string? Closest(string name, IEnumerable<string> candidates, int? bound = null)
    {
        var pool = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate)
                && !string.Equals(candidate, name, StringComparison.Ordinal))
            .ToList();

        //among prefix hits the shortest wins, being closest to the full name
        var prefixed = pool.Where(candidate => IsPrefix(name, candidate))
            .OrderBy(candidate => candidate.Length)
            .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (prefixed is not null)
            return prefixed;

        var limit = bound ?? Math.Max(2, name.Length / 2);
        var best = string.Empty;
        var bestDistance = int.MaxValue;

        foreach (var candidate in pool)
        {
            var distance = Distance(name, candidate);
            if (distance > limit)
                continue;

            //a tie goes to the shorter name, the one carrying fewer extra characters to strike out
            //without this the winner would be whichever the metadata happened to list first, so `TickRate` picked `SetRate` over `Rate`
            if (distance > bestDistance || (distance == bestDistance && candidate.Length >= best.Length))
                continue;

            bestDistance = distance;
            best = candidate;
        }

        return bestDistance == int.MaxValue ? null : best;
    }

    //IsPrefix checks whether a candidate starts with the name as an abbreviation, treating a single character as too broad
    private static bool IsPrefix(string name, string candidate)
        => name.Length >= 2 && candidate.StartsWith(name, StringComparison.OrdinalIgnoreCase);

    //Rank orders candidates by closeness and takes the first few
    public static List<string> Rank(string name, IEnumerable<string> candidates, int take, int? bound = null)
    {
        var limit = bound ?? Math.Max(2, name.Length / 2);
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate) && candidate != name)
            .Distinct(StringComparer.Ordinal)
            .Select(candidate => (Candidate: candidate, Distance: Distance(name, candidate)))
            .Where(item => item.Distance <= limit)
            .OrderBy(item => item.Distance)
            .Take(take)
            .Select(item => item.Candidate)
            .ToList();
    }

    //Distance computes the edit distance between two names, ignoring case
    public static int Distance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var column = 0; column <= b.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= a.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= b.Length; column++)
            {
                var cost = char.ToLowerInvariant(a[row - 1]) == char.ToLowerInvariant(b[column - 1]) ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

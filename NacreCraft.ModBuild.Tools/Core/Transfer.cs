namespace NacreCraft.ModBuild.Core;

//Download entry point; small pieces like catalogs are read in one shot while large files stream to disk, reporting byte progress to a callback
internal static class Transfer
{
    //Shared client with a relaxed timeout for large files
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private const int BufferSize = 128 * 1024;

    //Whether to draw a progress bar; skipped when output is redirected so control characters do not pollute pipes read by scripts
    internal static bool Enabled { get; set; } = !Console.IsOutputRedirected;

    //Fetch a small payload such as a few-KB catalog where a progress bar is not worth it
    public static byte[]? GetBytes(string url, out string error)
    {
        error = string.Empty;
        try
        {
            return Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"download {url} failed: {error}");
            return null;
        }
    }

    //Stream a download to the target through a temp file moved in place once complete; report fires per buffer and total is null when the server omits Content-Length
    public static bool DownloadToFile(string url, string target, Action<long, long?> report, out string error)
    {
        error = string.Empty;
        var temp = target + ".tmp";
        try
        {
            using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            using var source = response.Content.ReadAsStream();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using (var destination = File.Create(temp))
            {
                var buffer = new byte[BufferSize];
                long received = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    destination.Write(buffer, 0, read);
                    received += read;
                    report(received, total);
                }
            }

            File.Move(temp, target, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"download {url} failed: {error}");
            Discard(temp);
            return false;
        }
    }

    //Delete a half-written temp file, leaving it for the next overwrite when removal fails
    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException e)
        {
            Trace.Log($"cannot remove {path}: {e.Message}");
        }
    }
}

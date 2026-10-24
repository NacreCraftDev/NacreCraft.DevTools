using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//Approval records for tasks that replace a built-in tool
//The record is a fingerprint of the steps, so editing a command asks again
public static class TaskApprovals
{
    //Held under obj, which a clean removes together with the build output
    private const string Folder = "obj";

    private const string FileName = "task-approvals.json";

    //IsApproved whether the current steps of the task were confirmed once
    public static bool IsApproved(NcProject project, NcTask task)
        => Read(project).TryGetValue(task.Name, out var known)
           && string.Equals(known, Fingerprint(task), StringComparison.Ordinal);

    //Approve records the current steps as confirmed
    public static void Approve(NcProject project, NcTask task)
    {
        var approvals = Read(project);
        approvals[task.Name] = Fingerprint(task);
        Write(project, approvals);
    }

    //Fingerprint hashes the task content, so changed steps mean a different task
    //A script step also hashes its source, since editing the js file must ask again
    private static string Fingerprint(NcTask task)
        => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', task.Steps.Select(step =>
                step.Kind == NcStepKind.Script ? step.Text + "\n" + step.Source : step.Text)))));

    //Path of the approval records of one project
    private static string PathOf(NcProject project)
        => Path.Combine(project.Directory, Folder, FileName);

    //Read loads the records and treats an unreadable document as empty
    private static Dictionary<string, string> Read(NcProject project)
    {
        var path = PathOf(project);
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Trace.Log($"cannot read {path}: {e.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    //Write saves the records, and a failed write must not block the task
    private static void Write(NcProject project, Dictionary<string, string> approvals)
    {
        var path = PathOf(project);
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(approvals, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException e)
        {
            Trace.Log($"cannot write {path}: {e.Message}");
        }
    }
}

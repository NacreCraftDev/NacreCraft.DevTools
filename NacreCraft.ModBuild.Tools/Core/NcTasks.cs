using System.Xml;
using System.Xml.Linq;

namespace NacreCraft.ModBuild.Core;

//Reading and writing the task block of a project config
//A single task is replaced or appended, so the rest of the file stays exactly as the user wrote it
public static class NcTasks
{
    //Element holding the task list, matched without regard to case like the loader does
    private const string Element = "Tasks";

    //Element of one task inside that list
    private const string TaskElement = "Task";

    //Fragments returns the xml of each declared task, keyed by its name, which the page edits one at a time
    public static Dictionary<string, string> Fragments(string path)
    {
        var fragments = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var document = XDocument.Load(path);
            foreach (var task in Tasks(document.Root)?.Elements() ?? [])
            {
                if (!Matches(task, TaskElement))
                    continue;

                if ((string?)task.Attribute("Name") is { Length: > 0 } name)
                    fragments[name] = task.ToString();
            }
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            Trace.Log($"cannot read {path}: {e.Message}");
        }

        return fragments;
    }

    //SaveTask replaces the task named name, or appends the task when that name is empty
    //The xml has to be one <Task> element, and a name already used by another task is refused
    public static bool SaveTask(string path, string name, string xml, out string error)
    {
        error = string.Empty;

        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException e)
        {
            error = $"{path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{path} has no root element";
            return false;
        }

        XElement? task;
        try
        {
            task = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root;
        }
        catch (XmlException e)
        {
            error = $"the task xml is not valid xml: {e.Message}";
            return false;
        }

        if (task is null || !Matches(task, TaskElement))
        {
            error = $"the task xml must have a <{TaskElement}> root";
            return false;
        }

        if ((string?)task.Attribute("Name") is not { Length: > 0 } newName)
        {
            error = "the task needs a Name attribute";
            return false;
        }

        var tasks = Tasks(root);
        if (tasks is null)
        {
            tasks = new XElement(Element);
            root.Add(tasks);
        }

        var section = tasks;
        var clash = section.Elements().FirstOrDefault(element =>
            Matches(element, TaskElement)
            && string.Equals((string?)element.Attribute("Name"), newName, StringComparison.Ordinal)
            && !string.Equals((string?)element.Attribute("Name"), name, StringComparison.Ordinal));

        if (clash is not null)
        {
            error = $"task {newName} already exists";
            return false;
        }

        var existing = section.Elements().FirstOrDefault(element =>
            Matches(element, TaskElement)
            && string.Equals((string?)element.Attribute("Name"), name, StringComparison.Ordinal));

        if (existing is null)
            section.Add(task);
        else
            existing.ReplaceWith(task);

        try
        {
            XmlFile.Save(path, document);
            return true;
        }
        catch (IOException e)
        {
            error = $"cannot write {path}: {e.Message}";
            return false;
        }
    }

    //RemoveTask drops one declared task, leaving the rest of the file untouched
    public static bool RemoveTask(string path, string name, out string error)
    {
        error = string.Empty;

        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException e)
        {
            error = $"{path} is not valid xml: {e.Message}";
            return false;
        }

        var existing = Tasks(document.Root)?.Elements().FirstOrDefault(element =>
            Matches(element, TaskElement)
            && string.Equals((string?)element.Attribute("Name"), name, StringComparison.Ordinal));

        if (existing is null)
        {
            error = $"task {name} is not declared in {path}";
            return false;
        }

        existing.Remove();

        try
        {
            XmlFile.Save(path, document);
            return true;
        }
        catch (IOException e)
        {
            error = $"cannot write {path}: {e.Message}";
            return false;
        }
    }

    //SetOverride writes the default that lets a task replace a built-in tool, removing the attribute when turned off
    public static bool SetOverride(string path, bool value, out string error)
    {
        error = string.Empty;

        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException e)
        {
            error = $"{path} is not valid xml: {e.Message}";
            return false;
        }

        var root = document.Root;
        if (root is null)
        {
            error = $"{path} has no root element";
            return false;
        }

        if (value)
            root.SetAttributeValue("Override", "true");
        else
            root.Attribute("Override")?.Remove();

        try
        {
            XmlFile.Save(path, document);
            return true;
        }
        catch (IOException e)
        {
            error = $"cannot write {path}: {e.Message}";
            return false;
        }
    }

    //Tasks locates the task element under the root
    private static XElement? Tasks(XElement? root)
        => root?.Elements().FirstOrDefault(element => Matches(element, Element));

    //Matches tells whether an element carries the given name, ignoring case like the loader
    private static bool Matches(XElement element, string name)
        => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);
}

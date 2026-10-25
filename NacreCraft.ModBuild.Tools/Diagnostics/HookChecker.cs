using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NacreCraft.ModBuild.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NacreCraft.ModBuild.Diagnostics;

//HookSite where a rule was written down, enough to point a report at it
//A manifest rule has no position of its own, so its file is the manifest and the line is the first one, while the origin
//carried beside it names the entry
internal sealed record HookSite(string File, int Line, int Column, int Length, string SourceLine);

//HookRule one injection rule as the author wrote it, from a hooks entry or an Inject annotation
//Origin names the entry for a manifest rule and is empty for an annotation, whose site is already exact
internal sealed record HookRule(
    string Origin,
    HookSite Site,
    string Target,
    string Method,
    string Type,
    string PatchMode,
    string Placement,
    string Environment,
    string ReplaceType,
    string ReplaceMethod,
    string? InType,
    string? InMethod,
    string? SliceFrom,
    string? SliceTo,
    int? Ordinal,
    int? ArgumentIndex,
    int? LocalIndex,
    object? ConstantValue)
{
    //Where each field was written, so a report points at the field that is wrong rather than at the entry holding it
    //An annotation spells its members out in source and its site is already exact, so it leaves this empty
    internal IReadOnlyDictionary<string, HookSite> FieldSites { get; init; } =
        new Dictionary<string, HookSite>(StringComparer.Ordinal);

    //At the site of one field, falling back to the entry itself when that field was not written down
    internal HookSite At(string field) => FieldSites.TryGetValue(field, out var site) ? site : Site;

    //Dynamic whether the rule lands through runtime injection, which this check leaves alone
    public bool Dynamic => string.Equals(PatchMode, "RuntimeInject", StringComparison.OrdinalIgnoreCase);
}

//MixinRule one mixin as the author wrote it, moving the members of a source type onto a target type
internal sealed record MixinRule(string Origin, HookSite Site, string Target, string SourceType,
    IReadOnlyList<string> Interfaces)
{
    //Where each field was written, same purpose as on HookRule
    internal IReadOnlyDictionary<string, HookSite> FieldSites { get; init; } =
        new Dictionary<string, HookSite>(StringComparer.Ordinal);

    //At the site of one field, falling back to the entry itself when that field was not written down
    internal HookSite At(string field) => FieldSites.TryGetValue(field, out var site) ? site : Site;
}

//HookChecker reads a mod's injection rules and checks them against the types they name
//Rules are plain data to the loader, so a mistake in them only shows up at startup, when the mod is loaded and the target
//is missing or the replacement cannot carry the call; everything here is decidable from the source and the references
//The rules followed are the ones in NacreCraft/docs/injection.md
internal static class HookChecker
{
    //Code every problem carries, so the whole family is recognizable at a glance
    private const string Code = "NCH";

    //Injection forms the loader understands, also the vocabulary `ncm add hook` accepts
    internal static readonly string[] HookTypes =
    [
        "CallSite", "MethodBody", "NewObj", "FieldRead", "FieldWrite", "TypeCheck", "Box", "FunctionPointer",
        "LocalRead", "LocalWrite", "Constant", "CallArg", "Probe", "Mark",
    ];

    internal static readonly string[] PatchModes = ["ILRewrite", "RuntimeInject"];

    internal static readonly string[] Placements = ["Replace", "Before", "After"];

    internal static readonly string[] Environments = ["both", "client", "server"];

    //Fields a hooks entry may carry, which is the rule table of the injection guide
    private static readonly string[] HookFields =
    [
        "target", "method", "type", "patchMode", "placement", "environment", "label", "ordinal", "argumentIndex",
        "sliceFrom", "sliceTo", "inType", "inMethod", "localIndex", "constantValue", "replaceType", "replaceMethod",
    ];

    //The subset taking a number rather than a string
    private static readonly string[] NumberFields = ["ordinal", "argumentIndex", "localIndex"];

    //The subset taking a list rather than a single value
    private static readonly string[] ArrayFields = ["interfaces"];

    //Fields a mixins entry may carry
    private static readonly string[] MixinFields = ["target", "source", "interfaces"];

    //Lines of a manifest, read once so a report can show the line it points at
    private static readonly Dictionary<string, string[]> Lines = new(StringComparer.OrdinalIgnoreCase);

    //Absolute path of a manifest, since a report carries the path relative to the project and the process may run anywhere
    private static readonly Dictionary<string, string> Sources = new(StringComparer.OrdinalIgnoreCase);

    //Check validates every rule and reports through the bag, returning how many dynamic rules were left alone
    public static int Check(Compilation compilation, IReadOnlyList<HookRule> rules, IReadOnlyList<MixinRule> mixins,
        DiagnosticBag bag)
    {
        var dynamic = 0;
        foreach (var rule in rules)
        {
            //Runtime injection is another route with its own limits, so it stays out of this check
            if (rule.Dynamic)
            {
                dynamic++;
                continue;
            }

            CheckRule(compilation, rule, bag);
        }

        foreach (var mixin in mixins)
            CheckMixin(compilation, mixin, bag);

        return dynamic;
    }

    //WrittenAnnotations counts the Inject and Mixin annotations written in the project sources
    //Reading them from the syntax alone is deliberate: a project that turned the mod api off cannot resolve their type, so
    //the check that refuses them has to run before a compilation is even attempted
    public static int WrittenAnnotations(string root)
    {
        var count = 0;
        foreach (var path in SourceFiles.Enumerate(root))
        {
            var text = SourceTextCache.Read(path);
            if (text is null)
            {
                Trace.Log($"cannot read {path}");
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text, path: path);

            foreach (var attribute in tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>())
            {
                if (Named(attribute, "Inject") || Named(attribute, "Mixin"))
                    count++;
            }
        }

        return count;
    }

    //ReadManifest collects the hooks and mixins arrays of a manifest and reports what their entries get wrong
    //The document is handed over already parsed by ModProject, whose reader kept the line of every token, so a rule from
    //the manifest points at the entry it was written on instead of at the file as a whole
    public static void ReadManifest(JObject document, string manifestPath, string root, List<HookRule> rules,
        List<MixinRule> mixins, DiagnosticBag bag)
    {
        var file = Relative(root, manifestPath);
        Sources[file] = manifestPath;

        var hooks = Section(document, file, "hooks", bag);
        if (hooks is not null)
        {
            var index = 0;
            foreach (var token in hooks)
            {
                var origin = $"hooks[{index++}]";
                if (token is not JObject entry)
                {
                    ReportAt(DiagnosticSeverity.Error, $"{origin}: a rule has to be an object", SiteOf(token, file), bag,
                        "each entry carries at least `target` and `method`");
                    continue;
                }

                Fields(entry, file, origin, HookFields, NumberFields, [], bag);
                rules.Add(new HookRule(
                    origin,
                    SiteOf(entry, file),
                    Text(entry, "target"),
                    Text(entry, "method"),
                    Text(entry, "type", "CallSite"),
                    Text(entry, "patchMode", "ILRewrite"),
                    Text(entry, "placement", "Replace"),
                    Text(entry, "environment", "both"),
                    Text(entry, "replaceType"),
                    Text(entry, "replaceMethod"),
                    Optional(entry, "inType"),
                    Optional(entry, "inMethod"),
                    Optional(entry, "sliceFrom"),
                    Optional(entry, "sliceTo"),
                    Integer(entry, "ordinal"),
                    Integer(entry, "argumentIndex"),
                    Integer(entry, "localIndex"),
                    Constant(entry, "constantValue"))
                {
                    FieldSites = SitesOf(entry, file),
                });
            }
        }

        var mixinsSection = Section(document, file, "mixins", bag);
        if (mixinsSection is not null)
        {
            var index = 0;
            foreach (var token in mixinsSection)
            {
                var origin = $"mixins[{index++}]";
                if (token is not JObject entry)
                {
                    ReportAt(DiagnosticSeverity.Error, $"{origin}: a mixin has to be an object", SiteOf(token, file), bag,
                        "each entry carries `target` and `source`");
                    continue;
                }

                Fields(entry, file, origin, MixinFields, [], ArrayFields, bag);
                mixins.Add(new MixinRule(
                    origin,
                    SiteOf(entry, file),
                    Text(entry, "target"),
                    Text(entry, "source"),
                    Strings(entry, "interfaces"))
                {
                    FieldSites = SitesOf(entry, file),
                });
            }
        }
    }

    //Where every field of an entry was written, keyed by the name the manifest spells out
    //Only a manifest entry needs this; an annotation site already points at the attribute in source
    private static Dictionary<string, HookSite> SitesOf(JObject entry, string file)
    {
        var sites = new Dictionary<string, HookSite>(StringComparer.Ordinal);
        foreach (var property in entry.Properties())
            sites[property.Name] = SiteOf(property, file);
        return sites;
    }

    //Section reads one array of a manifest, reporting a section that is there but is not one
    private static JArray? Section(JObject document, string file, string name, DiagnosticBag bag)
    {
        if (!document.TryGetValue(name, out var value))
            return null;

        if (value is JArray array)
            return array;

        ReportAt(DiagnosticSeverity.Error, $"`{name}` has to be an array of entries", SiteOf(value, file), bag);
        return null;
    }

    //Fields checks one entry against what it may carry: a name that is not a field is reported, and so is a field whose
    //value is of the wrong kind, since the loader would either ignore it or fail on it
    private static void Fields(JObject entry, string file, string origin, string[] known, string[] numbers,
        string[] arrays, DiagnosticBag bag)
    {
        foreach (var property in entry.Properties())
        {
            var site = SiteOf(property, file);
            var name = property.Name;

            if (!known.Contains(name, StringComparer.Ordinal))
            {
                var near = Similarity.Closest(name, known);
                ReportAt(DiagnosticSeverity.Warning, $"{origin}: `{name}` is not a field of a rule", site, bag,
                    near is null ? string.Empty : $"did you mean `{near}`?");
                continue;
            }

            //a missing value is caught later, so only a value of the wrong kind is reported here
            if (property.Value.Type is JTokenType.Null or JTokenType.Undefined)
                continue;

            var wants = numbers.Contains(name, StringComparer.Ordinal) ? JTokenType.Integer
                : arrays.Contains(name, StringComparer.Ordinal) ? JTokenType.Array
                : JTokenType.String;
            if (property.Value.Type != wants)
            {
                var what = wants switch
                {
                    JTokenType.Integer => "a whole number",
                    JTokenType.Array => "a list",
                    _ => "a string",
                };

                ReportAt(DiagnosticSeverity.Error, $"{origin}: `{name}` has to be {what}", site, bag);
            }
        }
    }

    //ReadAnnotations collects every Inject and Mixin annotation written in the project sources
    //Reading them off the compilation keeps one source of truth with the check itself, and unlike the loader this runs
    //with the types at hand, which is what makes the signature check possible at all
    public static void ReadAnnotations(Compilation compilation, string root, List<HookRule> rules, List<MixinRule> mixins)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes())
            {
                switch (declaration)
                {
                    case MethodDeclarationSyntax method:
                        foreach (var attribute in AllAttributes(method.AttributeLists))
                        {
                            if (Named(attribute, "Inject"))
                                rules.Add(ReadInject(model, method, attribute, root));
                        }

                        break;
                    case TypeDeclarationSyntax type:
                        foreach (var attribute in AllAttributes(type.AttributeLists))
                        {
                            if (Named(attribute, "Mixin"))
                                mixins.Add(ReadMixin(model, type, attribute, root));
                        }

                        break;
                }
            }
        }
    }

    //ReadInject turns one Inject annotation into a rule; the replacement class and method are the ones it sits on
    private static HookRule ReadInject(SemanticModel model, MethodDeclarationSyntax method, AttributeSyntax attribute,
        string root)
    {
        var target = string.Empty;
        var targetMethod = string.Empty;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var position = 0;

        foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
        {
            if (argument.NameEquals is { } named)
            {
                values[named.Name.Identifier.ValueText] = ValueOf(model, argument.Expression);
                continue;
            }

            //the two leading arguments are the target type and the method on it
            if (position++ == 0)
                target = TypeNameOf(model, argument.Expression);
            else
                targetMethod = ValueOf(model, argument.Expression) as string ?? string.Empty;
        }

        var site = SiteOf(model, attribute, root);
        var declared = method.Parent as TypeDeclarationSyntax;

        return new HookRule(
            string.Empty,
            site,
            target,
            targetMethod,
            Setting(values, "HookType", "CallSite"),
            Setting(values, "PatchMode", "ILRewrite"),
            Setting(values, "Placement", "Replace"),
            Setting(values, "Environment", "both"),
            declared is null ? string.Empty : FullNameOf(model, declared),
            method.Identifier.ValueText,
            OptionalSetting(values, "InType"),
            OptionalSetting(values, "InMethod"),
            OptionalSetting(values, "SliceFrom"),
            OptionalSetting(values, "SliceTo"),
            Integer(values, "Ordinal"),
            Integer(values, "ArgumentIndex"),
            Integer(values, "LocalIndex"),
            values.GetValueOrDefault("ConstantValue"));
    }

    //ReadMixin turns one Mixin annotation into a rule
    private static MixinRule ReadMixin(SemanticModel model, TypeDeclarationSyntax type, AttributeSyntax attribute,
        string root)
    {
        var target = string.Empty;
        var contracts = new List<string>();

        foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
        {
            if (argument.NameEquals is { } named)
            {
                if (string.Equals(named.Name.Identifier.ValueText, "Interfaces", StringComparison.Ordinal))
                    contracts.AddRange(TypeNamesOf(model, argument.Expression));
                continue;
            }

            target = TypeNameOf(model, argument.Expression);
        }

        return new MixinRule(string.Empty, SiteOf(model, attribute, root), target, FullNameOf(model, type), contracts);
    }

    //CheckRule reports everything wrong with one rule, from the spelling of its fields down to the replacement signature
    private static void CheckRule(Compilation compilation, HookRule rule, DiagnosticBag bag)
    {
        var misspelled = false;
        misspelled |= !Known(rule.Type, HookTypes, "hook type", rule, bag);
        misspelled |= !Known(rule.PatchMode, PatchModes, "patch mode", rule, bag);
        misspelled |= !Known(rule.Placement, Placements, "placement", rule, bag);
        misspelled |= !Known(rule.Environment, Environments, "environment", rule, bag);

        //everything below reads the fields the spelling decides, so a misspelled one ends the check here
        if (misspelled)
            return;

        if (rule.Target.Length == 0 || rule.Method.Length == 0)
        {
            Report(DiagnosticSeverity.Error, "the rule names no target type or method", rule, bag,
                "every rule needs a `target` and a `method`");
            return;
        }

        //anchors a form cannot work without, since the position it edits is what they name
        switch (rule.Type)
        {
            case "LocalRead" or "LocalWrite" when rule.LocalIndex is null:
                Report(DiagnosticSeverity.Error, $"`{rule.Type}` needs `LocalIndex`", rule, bag,
                    "the form edits one local slot and has no other way to name it");
                return;
            case "Constant" when rule.ConstantValue is null:
                Report(DiagnosticSeverity.Error, "`Constant` needs `ConstantValue`", rule, bag,
                    "the form matches loads of one constant and has no other way to name it", field: "constantValue");
                return;
            case "CallArg" when rule.ArgumentIndex is null:
                Report(DiagnosticSeverity.Error, "`CallArg` needs `ArgumentIndex`", rule, bag,
                    "the form rewrites one argument and has no other way to name it", field: "argumentIndex");
                return;
        }

        foreach (var (field, name, value) in new[] { ("sliceFrom", "SliceFrom", rule.SliceFrom), ("sliceTo", "SliceTo", rule.SliceTo) })
        {
            if (!string.IsNullOrWhiteSpace(value) && !value.Contains("::", StringComparison.Ordinal))
            {
                Report(DiagnosticSeverity.Error, $"`{name}` reads `{value}`, which names no method", rule, bag,
                    "both fences are written `Full.Type::Method`", field: field);
            }
        }

        //the whole body is always replaced, so a placement on it cannot mean anything
        if (rule.Type == "MethodBody" && rule.Placement != "Replace")
            Report(DiagnosticSeverity.Warning, "`MethodBody` replaces the whole body, so its placement is ignored", rule, bag,
                field: "placement");

        var target = compilation.GetTypeByMetadataName(rule.Target);
        if (target is null)
        {
            //a target may live in a mod this project does not reference, which the check cannot resolve
            Report(DiagnosticSeverity.Warning,
                $"`{rule.Target}` is not in this project nor in the assemblies it references, so the rule was not checked",
                rule, bag, "the target has to be a kernel type or one of a mod this project references", field: "target");
            return;
        }

        var replacement = FindReplacement(compilation, rule, bag);
        if (replacement is null)
            return;

        CheckTaint(compilation, replacement, rule, bag);

        //for these forms the method name is the anchor itself, so it has to exist before anything else can be said
        if (!Anchored(rule.Type))
            return;

        var candidates = target.GetMembers(rule.Method).OfType<IMethodSymbol>().ToList();
        if (candidates.Count == 0)
        {
            var near = Similarity.Closest(rule.Method, target.GetMembers().OfType<IMethodSymbol>().Select(m => m.Name));
            Report(DiagnosticSeverity.Error, $"`{rule.Target}` has no member named `{rule.Method}`", rule, bag,
                near is null ? string.Empty : $"did you mean `{near}`?",
                near is null ? [] : [Suggestion.Text($"change the rule to target `{near}`")], field: "method");

            return;
        }

        CheckSignature(rule, candidates, replacement, bag);
    }

    //CheckTaint follows the replacement parameters to a dangerous api, which a signature check cannot see
    private static void CheckTaint(Compilation compilation, IMethodSymbol replacement, HookRule rule,
        DiagnosticBag bag)
    {
        if (replacement.Parameters.Length == 0)
            return;

        var options = new TaintOptions([], [], [], [], 4, true, true, true, 3, true, []);
        foreach (var path in TaintAnalysis.CheckMethod(compilation, replacement, options).Take(3))
        {
            var span = path.Sink.GetLineSpan();
            var location = $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}";
            Report(DiagnosticSeverity.Warning,
                $"the replacement can pass its parameters to {path.Text}", rule, bag,
                note: $"path: {path.Chain} ({location})");
        }
    }

    //CheckSignature compares the replacement method with what the form hands it
    private static void CheckSignature(HookRule rule, IReadOnlyList<IMethodSymbol> targets, IMethodSymbol replacement,
        DiagnosticBag bag)
    {
        switch (rule.Type)
        {
            //the callback takes the target's own parameters, with this in front when the call is an instance one
            case "CallSite" or "MethodBody":
                var wanted = targets.Select(method => method.Parameters.Length + (method.IsStatic ? 0 : 1))
                    .Distinct().Order().ToList();
                if (!wanted.Contains(replacement.Parameters.Length))
                {
                    Report(DiagnosticSeverity.Error,
                        $"the replacement takes {replacement.Parameters.Length} parameter(s) while {Display(targets[0])} "
                        + $"hands it {string.Join(" or ", wanted)}",
                        rule, bag, "an instance method passes its receiver as the first argument");
                }

                break;

            //Mark is called with the label alone and returns nothing
            case "Mark":
                if (!replacement.ReturnsVoid || replacement.Parameters.Length != 1
                    || replacement.Parameters[0].Type.SpecialType != SpecialType.System_String)
                {
                    Report(DiagnosticSeverity.Error, "`Mark` calls the replacement as `static void M(string label)`",
                        rule, bag);
                }

                break;

            //these two read a value out of the body and are called with nothing
            case "LocalRead" or "Constant" when replacement.Parameters.Length != 0:
                Report(DiagnosticSeverity.Error, $"`{rule.Type}` calls the replacement with no parameters", rule, bag);
                break;

            //this one hands the written value over
            case "LocalWrite" when replacement.Parameters.Length != 1:
                Report(DiagnosticSeverity.Error, "`LocalWrite` calls the replacement with the written value", rule, bag);
                break;

            //the host parameters come first, the value being rewritten last
            case "CallArg" when replacement.Parameters.Length < 1:
                Report(DiagnosticSeverity.Error, "`CallArg` hands the replacement the host parameters and the original value",
                    rule, bag);
                break;
        }
    }

    //CheckMixin validates a mixin: both types have to be known, and the source has to be written here
    private static void CheckMixin(Compilation compilation, MixinRule mixin, DiagnosticBag bag)
    {
        if (mixin.Target.Length == 0 || mixin.SourceType.Length == 0)
        {
            ReportMixin(DiagnosticSeverity.Error, "the mixin names no target or source type", mixin, bag);
            return;
        }

        if (compilation.GetTypeByMetadataName(mixin.Target) is null)
        {
            ReportMixin(DiagnosticSeverity.Warning,
                $"`{mixin.Target}` is not in this project nor in the assemblies it references, so the mixin was not checked",
                mixin, bag);
        }

        var source = compilation.GetTypeByMetadataName(mixin.SourceType);
        if (source is null || !source.Locations.Any(location => location.IsInSource))
        {
            ReportMixin(DiagnosticSeverity.Error,
                $"`{mixin.SourceType}` is not a type of this project, and a mixin source has to be written here", mixin, bag,
                "the members are moved onto the target, so they can only come from the mod itself", field: "source");
        }

        foreach (var name in mixin.Interfaces)
        {
            var contract = compilation.GetTypeByMetadataName(name);
            if (contract is null)
            {
                ReportMixin(DiagnosticSeverity.Error, $"`{name}` could not be resolved", mixin, bag);
                continue;
            }

            if (contract.TypeKind != TypeKind.Interface)
                ReportMixin(DiagnosticSeverity.Error, $"`{name}` is not an interface", mixin, bag);
        }
    }

    //FindReplacement locates the replacement method, which has to live in this project
    private static IMethodSymbol? FindReplacement(Compilation compilation, HookRule rule, DiagnosticBag bag)
    {
        if (rule.ReplaceType.Length == 0 || rule.ReplaceMethod.Length == 0)
        {
            Report(DiagnosticSeverity.Error, "the rule names no replacement class or method", rule, bag);
            return null;
        }

        var type = compilation.GetTypeByMetadataName(rule.ReplaceType);
        if (type is null)
        {
            Report(DiagnosticSeverity.Error, $"`{rule.ReplaceType}` is not a type of this project", rule, bag,
                "the class holding the replacement method has to be written here", field: "replaceType");
            return null;
        }

        var method = type.GetMembers(rule.ReplaceMethod).OfType<IMethodSymbol>().FirstOrDefault();
        if (method is null)
        {
            var near = Similarity.Closest(rule.ReplaceMethod, type.GetMembers().OfType<IMethodSymbol>().Select(m => m.Name));
            Report(DiagnosticSeverity.Error, $"`{rule.ReplaceType}` has no method named `{rule.ReplaceMethod}`", rule, bag,
                near is null ? string.Empty : $"did you mean `{near}`?", field: "replaceMethod");
            return null;
        }

        return method;
    }

    //Anchored whether the form names a method on the target, which is what makes the method name checkable
    private static bool Anchored(string type)
        => type is "CallSite" or "MethodBody" or "CallArg" or "LocalRead" or "LocalWrite" or "Constant" or "Probe" or "Mark";

    //Known checks one field against its vocabulary
    private static bool Known(string value, string[] allowed, string what, HookRule rule, DiagnosticBag bag)
    {
        if (allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            return true;

        var near = Similarity.Closest(value, allowed);
        Report(DiagnosticSeverity.Error, $"`{value}` is not a known {what}", rule, bag,
            $"the accepted ones are {string.Join(", ", allowed)}",
            near is null ? [] : [Suggestion.Text($"change it to `{near}`")]);

        return false;
    }

    //Display names a method the way a reader recognizes it
    private static string Display(IMethodSymbol method)
        => $"{method.ContainingType.Name}.{method.Name}({string.Join(", ", method.Parameters.Select(p => p.Type.Name))})";

    //AllAttributes flattens the attribute lists of one declaration
    private static IEnumerable<AttributeSyntax> AllAttributes(SyntaxList<AttributeListSyntax> lists)
        => lists.SelectMany(list => list.Attributes);

    //Named whether an attribute is the one asked for, by its written name or a qualified form of it
    private static bool Named(AttributeSyntax attribute, string name)
    {
        var written = attribute.Name.ToString();
        return written.Equals(name, StringComparison.Ordinal)
            || written.Equals(name + "Attribute", StringComparison.Ordinal)
            || written.EndsWith("." + name, StringComparison.Ordinal)
            || written.EndsWith("." + name + "Attribute", StringComparison.Ordinal);
    }

    //SiteOf locates an attribute in its file
    private static HookSite SiteOf(SemanticModel model, AttributeSyntax attribute, string root)
    {
        var span = attribute.GetLocation().GetLineSpan();
        var line = span.StartLinePosition.Line;
        var text = model.SyntaxTree.GetText().Lines;
        var source = line >= 0 && line < text.Count ? text[line].ToString() : string.Empty;
        return new HookSite(Relative(root, span.Path), line + 1, span.StartLinePosition.Character + 1,
            attribute.Name.Span.Length, source);
    }

    private static string Relative(string root, string path)
        => path.Length == 0 ? path : Path.GetRelativePath(root, path).Replace('\\', '/');

    //TypeNameOf reads the full name an argument refers to, accepting typeof(X) as well as a written name
    private static string TypeNameOf(SemanticModel model, ExpressionSyntax expression)
    {
        if (expression is TypeOfExpressionSyntax typeOf)
            return model.GetTypeInfo(typeOf.Type).Type?.ToDisplayString() ?? string.Empty;

        return ValueOf(model, expression) as string ?? string.Empty;
    }

    //TypeNamesOf reads a list of type names out of an array initializer
    private static IEnumerable<string> TypeNamesOf(SemanticModel model, ExpressionSyntax expression)
    {
        var elements = expression switch
        {
            ImplicitArrayCreationExpressionSyntax implicitArray => implicitArray.Initializer.Expressions,
            ArrayCreationExpressionSyntax created => created.Initializer?.Expressions ?? default,
            _ => default,
        };

        return elements.Select(element => TypeNameOf(model, element)).Where(name => name.Length > 0);
    }

    //FullNameOf reads the declared full name of a type
    private static string FullNameOf(SemanticModel model, TypeDeclarationSyntax type)
        => model.GetDeclaredSymbol(type)?.ToDisplayString() ?? type.Identifier.ValueText;

    //ValueOf reads a constant value out of an argument expression
    private static object? ValueOf(SemanticModel model, ExpressionSyntax expression)
        => model.GetConstantValue(expression) is { HasValue: true } constant ? constant.Value : null;

    //Setting reads a named argument as a string, falling back when it is absent
    private static string Setting(Dictionary<string, object?> values, string name, string fallback)
        => values.GetValueOrDefault(name) as string ?? fallback;

    //OptionalSetting reads a named argument that may simply not be written
    private static string? OptionalSetting(Dictionary<string, object?> values, string name)
        => values.GetValueOrDefault(name) as string;

    //Integer reads a named argument written as a number, which the annotation declares as object
    private static int? Integer(Dictionary<string, object?> values, string name)
        => values.GetValueOrDefault(name) switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            _ => null,
        };

    private static void Report(DiagnosticSeverity severity, string message, HookRule rule, DiagnosticBag bag,
        string note = "", IReadOnlyList<Suggestion>? fixes = null, string field = "")
        //a manifest rule names its entry, an annotation site is exact already
        //a named field takes precedence, so the report points at the value that is wrong
        => ReportAt(severity, rule.Origin.Length == 0 ? message : rule.Origin + ": " + message,
            field.Length == 0 ? rule.Site : rule.At(field), bag, note, fixes);

    private static void ReportMixin(DiagnosticSeverity severity, string message, MixinRule mixin, DiagnosticBag bag,
        string note = "", string field = "")
        => ReportAt(severity, mixin.Origin.Length == 0 ? message : mixin.Origin + ": " + message,
            field.Length == 0 ? mixin.Site : mixin.At(field), bag, note);

    //ReportAt is the single way a problem reaches the bag, for rules and for the manifest structure alike
    private static void ReportAt(DiagnosticSeverity severity, string message, HookSite site, DiagnosticBag bag,
        string note = "", IReadOnlyList<Suggestion>? fixes = null)
        => bag.Add(new Diagnostic(severity, Code, message, site.File, site.Line, site.Column, site.Length,
            site.SourceLine, note, string.Empty, fixes ?? []));

    //SiteOf locates a manifest token at the line its reader recorded, with the line itself so the report can show it
    private static HookSite SiteOf(JToken token, string file)
    {
        var info = token as IJsonLineInfo;
        var known = info is not null && info.HasLineInfo();
        var line = known ? info!.LineNumber : 1;
        var column = known ? info!.LinePosition : 1;
        return new HookSite(file, line, column, 1, LineOf(file, line));
    }

    //a property points at its own name rather than at the colon the token reports, which is a one character mark
    private static HookSite SiteOf(JProperty property, string file)
    {
        var site = SiteOf((JToken)property, file);
        var index = site.SourceLine.IndexOf($"\"{property.Name}\"", StringComparison.Ordinal);
        return index < 0
            ? site
            : site with { Column = index + 1, Length = property.Name.Length + 2 };
    }

    //LineOf reads one line of a manifest, keeping the file read once for the whole check
    private static string LineOf(string file, int line)
    {
        var path = Sources.TryGetValue(file, out var absolute) ? absolute : file;
        if (!Lines.TryGetValue(path, out var lines))
        {
            var text = SourceTextCache.Read(path);
            lines = text is null ? [] : text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
                lines[index] = lines[index].TrimEnd('\r');
            Lines[path] = lines;
        }

        //the line as written, indentation included, so the caret the renderer draws lands under the right column
        return line >= 1 && line <= lines.Length ? lines[line - 1] : string.Empty;
    }

    //Text reads a string field, falling back when it is missing or of another kind
    private static string Text(JObject entry, string name, string fallback = "")
        => entry.TryGetValue(name, out var value) && value.Type == JTokenType.String
            ? value.Value<string>() ?? fallback
            : fallback;

    //Optional reads a string field as null when it is absent or empty
    private static string? Optional(JObject entry, string name)
    {
        var text = Text(entry, name);
        return text.Length == 0 ? null : text;
    }

    private static int? Integer(JObject entry, string name)
        => entry.TryGetValue(name, out var value) && value.Type == JTokenType.Integer
            ? value.Value<int>()
            : null;

    //Constant reads a constant, keeping the boxed type the loader compares by
    private static object? Constant(JObject entry, string name)
    {
        if (!entry.TryGetValue(name, out var value))
            return null;

        if (value.Type == JTokenType.Integer)
        {
            //the loader takes a whole number as int while it fits and as long afterwards, so 5 and 5L reach different anchors
            var wide = value.Value<long>();
            return wide is >= int.MinValue and <= int.MaxValue ? (int)wide : wide;
        }

        return value.Type switch
        {
            JTokenType.Float => value.Value<double>(),
            JTokenType.String => value.Value<string>(),
            JTokenType.Boolean => value.Value<bool>(),
            _ => null,
        };
    }

    //Strings reads an array of strings, treating anything else as absent
    private static IReadOnlyList<string> Strings(JObject entry, string name)
    {
        if (!entry.TryGetValue(name, out var value) || value is not JArray array)
            return [];

        return array.Where(item => item.Type == JTokenType.String)
            .Select(item => item.Value<string>() ?? string.Empty)
            .Where(text => text.Length > 0)
            .ToList();
    }
}

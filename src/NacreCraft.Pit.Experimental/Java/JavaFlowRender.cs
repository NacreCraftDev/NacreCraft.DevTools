using System.Text;

namespace NacreCraft.Pit.Experimental.Java;

//JavaFlowRender prints each class as an ordered outline of what runs, following the shipped flow layout
//The nodes come from the tree-sitter syntax tree, so the order is the source order
internal static class JavaFlowRender
{
    //MaxLines caps one method, so a generated body cannot fill the whole terminal
    private const int MaxLines = 4000;

    //MaxDepth is the safety limit when the caller asks for no limit
    private const int MaxDepth = 64;

    private const string Reset = "\u001b[0m";
    private const string DimColor = "\u001b[90m";
    private const string ClassColor = "\u001b[36m";
    private const string MethodColor = "\u001b[1;37m";
    private const string ControlColor = "\u001b[33m";
    private const string CallColor = "\u001b[32m";
    private const string ReturnColor = "\u001b[35m";
    private const string ErrorColor = "\u001b[31m";

    private const string BodyPrefix = "      ";

    public static string Render(IReadOnlyList<JavaTypeFlow> types, string root, string? className, bool all, int depth,
        bool color, out string error)
    {
        error = string.Empty;

        if (className is not null)
        {
            var matches = types.Where(type => Matches(type, className)).ToList();
            if (matches.Count == 0)
            {
                error = $"no class '{className}' in the selected sources";
                return string.Empty;
            }

            if (matches.Count > 1)
            {
                var names = string.Join(Environment.NewLine,
                    matches.Select(type => $"  {type.Display} ({type.File}:{type.Line})"));
                error = $"class name is ambiguous, name one of these:{Environment.NewLine}{names}";
                return string.Empty;
            }

            types = matches;
        }

        var text = new StringBuilder();
        text.AppendLine(Paint($"flow for {root}", ClassColor, color));
        text.AppendLine();
        if (types.Count == 0)
        {
            text.AppendLine(Paint("  (no class)", DimColor, color));
            return text.ToString();
        }

        var limit = depth > 0 ? Math.Min(depth, MaxDepth) : MaxDepth;
        var expand = className is not null || all;

        foreach (var type in types.OrderBy(item => item.Display, StringComparer.Ordinal))
        {
            text.AppendLine(
                $"  {Paint(type.Display, ClassColor, color)} {Paint($"({type.File}:{type.Line})", DimColor, color)}");

            if (type.Methods.Count == 0)
            {
                text.AppendLine(Paint("    (no method)", DimColor, color));
                text.AppendLine();
                continue;
            }

            foreach (var method in type.Methods)
            {
                text.AppendLine($"    {Paint(method.Signature, MethodColor, color)}");
                if (!expand)
                    continue;

                if (method.Body is null)
                {
                    text.AppendLine(Paint("      (no body)", DimColor, color));
                    continue;
                }

                var state = new State(text, limit, color);
                Statements(method.Body, state, 0, BodyPrefix);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static bool Matches(JavaTypeFlow type, string name)
    {
        var simple = type.Display.LastIndexOf('.') < 0
            ? type.Display
            : type.Display[(type.Display.LastIndexOf('.') + 1)..];

        return simple.Equals(name, StringComparison.OrdinalIgnoreCase)
            || type.Display.Equals(name, StringComparison.OrdinalIgnoreCase)
            || type.Display.Replace('.', '/').Equals(name.Replace('.', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private static void Statements(JavaFlowNode block, State state, int depth, string prefix, bool tail = false)
    {
        var list = block.Children.Where(child => StatementRules.Contains(child.Rule)).ToList();
        for (var index = 0; index < list.Count; index++)
        {
            if (state.Stopped)
                return;

            Render(list[index], state, depth, prefix, !tail && index == list.Count - 1);
        }
    }

    private static void Render(JavaFlowNode node, State state, int depth, string prefix, bool last)
    {
        if (state.Stopped)
            return;

        if (depth > state.Limit)
        {
            WriteLine(state, prefix, last, "...", null);
            return;
        }

        switch (node.Rule)
        {
            case "block":
            case "constructor_body":
                Statements(node, state, depth, prefix);
                break;
            case "local_variable_declaration":
                Declarations(node, state, prefix, last);
                break;
            case "expression_statement":
                Expression(node, state, prefix, last);
                break;
            case "if_statement":
                If(node, state, depth, prefix, last);
                break;
            case "for_statement":
            case "enhanced_for_statement":
            case "while_statement":
            case "do_statement":
                Loop(node, state, depth, prefix, last);
                break;
            case "switch_expression":
                Switch(node, state, depth, prefix, last);
                break;
            case "try_statement":
                Try(node, state, depth, prefix, last);
                break;
            case "synchronized_statement":
                Lock(node, state, depth, prefix, last);
                break;
            case "return_statement":
                WriteLine(state, prefix, last, "RETURN", Keyword(node.Text, "return"));
                break;
            case "throw_statement":
                WriteLine(state, prefix, last, "THROW", Keyword(node.Text, "throw"));
                break;
            case "break_statement":
                WriteLine(state, prefix, last, "BREAK", Keyword(node.Text, "break"));
                break;
            case "continue_statement":
                WriteLine(state, prefix, last, "CONTINUE", Keyword(node.Text, "continue"));
                break;
            case "assert_statement":
                WriteLine(state, prefix, last, "ASSERT", Keyword(node.Text, "assert"));
                break;
            case "labeled_statement":
                Labeled(node, state, depth, prefix, last);
                break;
            case "lambda_expression":
                WriteLine(state, prefix, last, "LAMBDA", node.Text);
                break;
            case "local_class_declaration":
                WriteLine(state, prefix, last, "CLASS", node.Text);
                break;
            default:
                WriteLine(state, prefix, last, "EXPR", node.Text);
                break;
        }
    }

    private static void Declarations(JavaFlowNode node, State state, string prefix, bool last)
    {
        var declarators = node.Children.Where(child => child.Rule == "variable_declarator").ToList();
        if (declarators.Count == 0)
        {
            WriteLine(state, prefix, last, "DECLARE", node.Text);
            return;
        }

        for (var index = 0; index < declarators.Count; index++)
        {
            var declarator = declarators[index];
            var name = declarator.Children.FirstOrDefault(child => child.Rule == "identifier")?.Text ?? declarator.Text;
            var value = declarator.Children.FirstOrDefault(child => child.Rule != "identifier");
            var lastOne = last && index == declarators.Count - 1;

            if (value is null)
                WriteLine(state, prefix, lastOne, "DECLARE", name);
            else
                WriteLine(state, prefix, lastOne, "SET", $"{name} = {value.Text}");
        }
    }

    private static void Expression(JavaFlowNode node, State state, string prefix, bool last)
    {
        var child = node.Children.FirstOrDefault();
        if (child is null)
        {
            WriteLine(state, prefix, last, "EXPR", node.Text);
            return;
        }

        var label = child.Rule switch
        {
            "method_invocation" => "CALL",
            "object_creation_expression" => "NEW",
            "assignment_expression" or "update_expression" => "SET",
            "lambda_expression" => "LAMBDA",
            _ => "EXPR",
        };

        WriteLine(state, prefix, last, label, child.Text);
    }

    private static void If(JavaFlowNode node, State state, int depth, string prefix, bool last)
    {
        var condition = node.Children.FirstOrDefault(child => child.Rule == "parenthesized_expression");
        var rest = node.Children.Where(child => child.Rule != "parenthesized_expression").ToList();
        var then = rest.ElementAtOrDefault(0);
        var otherwise = rest.ElementAtOrDefault(1);
        var hasElse = otherwise is not null;

        WriteLine(state, prefix, hasElse ? false : last, "IF", Trim(condition?.Text));
        if (then is not null)
        {
            Body(then, state, depth + 1, prefix + (hasElse ? "│   " : last ? "    " : "│   "));
        }

        if (!hasElse)
            return;

        WriteLine(state, prefix, last, "ELSE", null);
        Body(otherwise!, state, depth + 1, Child(prefix, last));
    }

    private static void Loop(JavaFlowNode node, State state, int depth, string prefix, bool last)
    {
        var body = node.Children.LastOrDefault(child => child.Rule is "block" or "statement");
        WriteLine(state, prefix, last, "LOOP", Header(node));
        if (body is not null)
            Body(body, state, depth + 1, Child(prefix, last));
    }

    private static void Switch(JavaFlowNode node, State state, int depth, string prefix, bool last)
    {
        var value = node.Children.FirstOrDefault(child => child.Rule == "parenthesized_expression");
        var block = node.Children.FirstOrDefault(child => child.Rule == "switch_block");
        WriteLine(state, prefix, last, "SWITCH", Trim(value?.Text));
        if (block is null)
            return;

        var child = Child(prefix, last);
        var cases = block.Children.Where(item => item.Rule is "switch_rule" or "switch_block_statement_group")
            .ToList();
        for (var index = 0; index < cases.Count; index++)
        {
            var item = cases[index];
            var lastCase = index == cases.Count - 1;
            var raw = item.Children.FirstOrDefault(entry => entry.Rule == "switch_label")?.Text ?? "case";
            var label = raw.StartsWith("case ", StringComparison.Ordinal) ? raw[5..].Trim() : raw;
            WriteLine(state, child, lastCase, "CASE", label);

            var body = new JavaFlowNode("block", string.Empty, item.Line);
            foreach (var entry in item.Children.Where(entry => entry.Rule != "switch_label"))
                body.Children.Add(entry);

            Statements(body, state, depth + 1, Child(child, lastCase));
        }
    }

    private static void Try(JavaFlowNode node, State state, int depth, string prefix, bool last)
    {
        var body = node.Children.FirstOrDefault(child => child.Rule == "block");
        var catches = node.Children.Where(child => child.Rule == "catch_clause").ToList();
        var finallyClause = node.Children.FirstOrDefault(child => child.Rule == "finally_clause");

        WriteLine(state, prefix, last, "TRY", null);
        var child = Child(prefix, last);
        if (body is not null)
        {
            //the body is not the last child of the try, so its last statement keeps a branch open for catch and finally
            Statements(body, state, depth + 1, child, catches.Count > 0 || finallyClause is not null);
        }

        for (var index = 0; index < catches.Count; index++)
        {
            var handler = catches[index];
            var lastCatch = index == catches.Count - 1 && finallyClause is null;
            var parameter = handler.Children.FirstOrDefault(entry => entry.Rule == "catch_formal_parameter");
            WriteLine(state, child, lastCatch, "CATCH", parameter?.Text);
            var handlerBody = handler.Children.FirstOrDefault(entry => entry.Rule == "block");
            if (handlerBody is not null)
                Statements(handlerBody, state, depth + 2, Child(child, lastCatch));
        }

        if (finallyClause is null)
            return;

        WriteLine(state, child, true, "FINALLY", null);
        var finallyBody = finallyClause.Children.FirstOrDefault(entry => entry.Rule == "block");
        if (finallyBody is not null)
            Statements(finallyBody, state, depth + 2, child + "    ");
    }

    private static void Lock(JavaFlowNode node, State state, int depth, string prefix, bool last)
    {
        var value = node.Children.FirstOrDefault(child => child.Rule == "parenthesized_expression");
        var body = node.Children.FirstOrDefault(child => child.Rule == "block");
        WriteLine(state, prefix, last, "LOCK", Trim(value?.Text));
        if (body is not null)
            Statements(body, state, depth + 1, Child(prefix, last));
    }

    private static void Labeled(JavaFlowNode node, State state, int depth, string prefix, bool last)
    {
        var name = node.Children.FirstOrDefault(child => child.Rule == "identifier");
        var statement = node.Children.FirstOrDefault(child => child.Rule != "identifier");
        WriteLine(state, prefix, statement is null ? last : false, "LABEL", name?.Text);
        if (statement is not null)
            Render(statement, state, depth + 1, prefix + (last ? "    " : "│   "), true);
    }

    //Body renders a nested block or a single statement, the two shapes a branch can take
    private static void Body(JavaFlowNode node, State state, int depth, string prefix)
    {
        if (node.Rule is "block" or "constructor_body")
        {
            Statements(node, state, depth, prefix);
            return;
        }

        Render(node, state, depth, prefix, true);
    }

    private static string Child(string prefix, bool last) => prefix + (last ? "    " : "│   ");

    //Header keeps the control statement up to its opening brace, which is what the label shows
    private static string Header(JavaFlowNode node)
    {
        var brace = node.Text.IndexOf('{');
        return brace < 0 ? node.Text : node.Text[..brace].TrimEnd();
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text.Length > 1 && text[0] == '(' && text[^1] == ')' ? text[1..^1] : text;
    }

    //Keyword drops the leading word so the label does not repeat it
    private static string Keyword(string text, string keyword)
    {
        if (!text.StartsWith(keyword, StringComparison.Ordinal))
            return text;

        var rest = text[keyword.Length..].TrimStart();
        return rest.Length == 0 ? string.Empty : rest.TrimEnd(';');
    }

    private static string Paint(string text, string color, bool enabled)
        => enabled ? color + text + Reset : text;

    private static string LabelColor(string label) => label switch
    {
        "IF" or "ELSE" or "LOOP" or "SWITCH" or "CASE" or "TRY" or "CATCH" or "FINALLY"
            or "LOCK" or "LABEL" or "ASSERT" or "BREAK" or "CONTINUE" => ControlColor,
        "CALL" or "NEW" or "LAMBDA" => CallColor,
        "SET" or "DECLARE" or "EXPR" or "CLASS" => DimColor,
        "RETURN" => ReturnColor,
        "THROW" => ErrorColor,
        _ => string.Empty,
    };

    private static void WriteLine(State state, string prefix, bool last, string label, string? detail)
    {
        if (state.Stopped)
            return;

        if (state.Lines >= MaxLines)
        {
            state.Stopped = true;
            state.Text.AppendLine(Paint("      ... line limit reached", DimColor, state.Color));
            return;
        }

        if (state.Color)
        {
            state.Text.Append(DimColor).Append(prefix).Append(last ? "└─ " : "├─ ").Append(Reset);
            var tone = LabelColor(label);
            if (tone.Length > 0)
                state.Text.Append(tone);
            state.Text.Append(label);
            if (tone.Length > 0)
                state.Text.Append(Reset);
        }
        else
        {
            state.Text.Append(prefix).Append(last ? "└─ " : "├─ ").Append(label);
        }

        if (!string.IsNullOrEmpty(detail))
            state.Text.Append(' ').Append(detail);
        state.Text.AppendLine();
        state.Lines++;
    }

    private static readonly HashSet<string> StatementRules = new(StringComparer.Ordinal)
    {
        "block",
        "local_variable_declaration",
        "expression_statement",
        "if_statement",
        "for_statement",
        "enhanced_for_statement",
        "while_statement",
        "do_statement",
        "switch_expression",
        "try_statement",
        "synchronized_statement",
        "return_statement",
        "throw_statement",
        "break_statement",
        "continue_statement",
        "assert_statement",
        "labeled_statement",
        "lambda_expression",
        "local_class_declaration",
    };

    private sealed class State(StringBuilder text, int limit, bool color)
    {
        public StringBuilder Text { get; } = text;

        public int Limit { get; } = limit;

        public bool Color { get; } = color;

        public int Lines { get; set; }

        public bool Stopped { get; set; }
    }
}

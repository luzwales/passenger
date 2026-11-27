using System.Text;

namespace Passenger.Linq;

/// <summary>Projected virtual C# source generated from a <see cref="LinqDocument"/> with bidirectional
/// 0-based line and character-offset mapping between the original .linq file and the generated C# file.</summary>
public sealed record LinqProjectedSource(
    string CSharpCode,
    int PreambleLines,
    int BodyStartLineInLinq,
    int BodyLineCount,
    int PreambleChars = 0,
    int BodyStartCharInLinq = 0,
    int BodyChars = 0)
{
    /// <summary>Maps a 0-based line in the generated C# file back to the 0-based line in the .linq file,
    /// or null if the line belongs to synthetic preamble/epilogue.</summary>
    public int? ToLinqLine(int generatedLine)
    {
        int offset = generatedLine - PreambleLines;
        if (offset < 0 || offset >= BodyLineCount)
        {
            return null;
        }
        return BodyStartLineInLinq + offset;
    }

    /// <summary>Maps a 0-based line in the .linq file to the 0-based line in the generated C# file,
    /// or null if the line is inside the XML header.</summary>
    public int? ToGeneratedLine(int linqLine)
    {
        int offset = linqLine - BodyStartLineInLinq;
        if (offset < 0 || offset >= BodyLineCount)
        {
            return null;
        }
        return PreambleLines + offset;
    }

    /// <summary>Maps a 0-based character offset in the .linq file to the generated C# file.</summary>
    public int ToGeneratedOffset(int linqOffset)
    {
        int rel = Math.Clamp(linqOffset - BodyStartCharInLinq, 0, BodyChars);
        return PreambleChars + rel;
    }

    /// <summary>Maps a 0-based character offset in the generated C# file back to the .linq file.</summary>
    public int ToLinqOffset(int generatedOffset)
    {
        int rel = Math.Clamp(generatedOffset - PreambleChars, 0, BodyChars);
        return BodyStartCharInLinq + rel;
    }
}

/// <summary>Projects a .linq document into a valid SDK file-based C# program (or Roslyn virtual buffer)
/// carrying Passenger.Linq.Runtime, optional DataContext injection, and exact line mapping.</summary>
public static class LinqPreprocessor
{
    private static readonly string[] DefaultUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Text",
        "System.Threading.Tasks",
        "Passenger.Linq.Runtime",
    ];

    public static LinqProjectedSource Project(
        LinqDocument doc,
        string runtimeAssemblyPath,
        string? generatedContextFile = null,
        string? contextTypeName = null,
        string? connectionStringEnvVar = null,
        bool includeRunDirectives = true,
        IReadOnlyList<string>? tablePropertyNames = null,
        string? sourceFilePathForLineDirective = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        StringBuilder sb = new();
        int preambleLines = 0;

        void AppendLine(string line)
        {
            sb.Append(line).Append('\n');
            preambleLines++;
        }

        if (includeRunDirectives)
        {
            AppendLine($"#:ref {runtimeAssemblyPath}");
            foreach (LinqPackageRef pkg in doc.Packages)
            {
                AppendLine(string.IsNullOrWhiteSpace(pkg.Version)
                    ? $"#:package {pkg.Name}"
                    : $"#:package {pkg.Name}@{pkg.Version}");
            }
            foreach (string reference in doc.References)
            {
                AppendLine($"#:ref {reference}");
            }
            if (!string.IsNullOrWhiteSpace(generatedContextFile))
            {
                AppendLine($"#:include {generatedContextFile}");
            }
        }

        HashSet<string> seenUsings = new(StringComparer.Ordinal);
        foreach (string u in DefaultUsings)
        {
            if (seenUsings.Add(u))
            {
                AppendLine($"using {u};");
            }
        }
        foreach (string u in doc.Namespaces)
        {
            if (seenUsings.Add(u))
            {
                AppendLine($"using {u};");
            }
        }
        AppendLine("using static Passenger.Linq.Runtime.LinqExtensions;");

        if (!string.IsNullOrWhiteSpace(contextTypeName))
        {
            if (string.IsNullOrWhiteSpace(connectionStringEnvVar))
            {
                AppendLine($"using var __ctx = new {contextTypeName}();");
            }
            else
            {
                string envExpr = $"Environment.GetEnvironmentVariable(\"{EscapeString(connectionStringEnvVar)}\") ?? string.Empty";
                AppendLine($"using var __ctx = new {contextTypeName}({envExpr});");
            }
            if (tablePropertyNames is { Count: > 0 })
            {
                foreach (string prop in tablePropertyNames)
                {
                    AppendLine($"var {prop} = __ctx.{prop};");
                }
            }
        }

        string body = doc.Body.Replace("\r\n", "\n").Replace('\r', '\n');
        int bodyLineCount = CountLines(body);
        bool emitLineDirective = !string.IsNullOrWhiteSpace(sourceFilePathForLineDirective);

        if (doc.Kind == LinqQueryKind.Expression)
        {
            string trimmed = body.TrimEnd();
            if (trimmed.EndsWith(';'))
            {
                trimmed = trimmed[..^1].TrimEnd();
            }
            int trimmedLines = CountLines(trimmed);
            bodyLineCount = trimmedLines;
            AppendLine("var __linqResult = (");
            if (emitLineDirective)
            {
                AppendLine($"#line {doc.BodyStartLine + 1} \"{EscapeString(sourceFilePathForLineDirective!)}\"");
            }
            int preambleChars = sb.Length;
            sb.Append(trimmed).Append('\n');
            if (emitLineDirective)
            {
                sb.Append("#line default\n");
            }
            sb.Append(");\n");
            sb.Append("__linqResult.Dump();\n");
            return new LinqProjectedSource(sb.ToString(), preambleLines, doc.BodyStartLine, bodyLineCount, preambleChars, doc.BodyStartChar, trimmed.Length);
        }
        else
        {
            if (emitLineDirective)
            {
                AppendLine($"#line {doc.BodyStartLine + 1} \"{EscapeString(sourceFilePathForLineDirective!)}\"");
            }
            int preambleChars = sb.Length;
            sb.Append(body);
            if (!body.EndsWith('\n'))
            {
                sb.Append('\n');
            }
            if (emitLineDirective)
            {
                sb.Append("#line default\n");
            }
            return new LinqProjectedSource(sb.ToString(), preambleLines, doc.BodyStartLine, bodyLineCount, preambleChars, doc.BodyStartChar, body.Length);
        }
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }
        int lines = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }
        if (text.EndsWith('\n'))
        {
            lines--;
        }
        return Math.Max(1, lines);
    }

    private static string EscapeString(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

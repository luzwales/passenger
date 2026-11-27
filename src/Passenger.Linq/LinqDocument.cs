using System.Xml.Linq;

namespace Passenger.Linq;

/// <summary>Execution mode of a .linq script, matching LINQPad's &lt;Query Kind="..."&gt; header.</summary>
public enum LinqQueryKind
{
    Statements,
    Expression,
    Program,
}

/// <summary>A NuGet package reference declared in a .linq header or Passenger #:package directive.</summary>
public sealed record LinqPackageRef(string Name, string? Version);

/// <summary>Parsed representation of a .linq file: XML &lt;Query&gt; metadata header + C# body.</summary>
public sealed record LinqDocument(
    LinqQueryKind Kind,
    string? ConnectionId,
    string? ConnectionName,
    IReadOnlyList<string> Namespaces,
    IReadOnlyList<LinqPackageRef> Packages,
    IReadOnlyList<string> References,
    string Body,
    int BodyStartLine,
    bool HasXmlHeader,
    int BodyStartChar = 0)
{
    public static LinqDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        int pos = 0;
        if (normalized.StartsWith('\uFEFF'))
        {
            pos = 1;
        }
        while (pos < normalized.Length && (normalized[pos] == ' ' || normalized[pos] == '\t' || normalized[pos] == '\n'))
        {
            pos++;
        }

        if (!normalized.AsSpan(pos).StartsWith("<Query", StringComparison.Ordinal))
        {
            LinqQueryKind inferred = InferKindWithoutHeader(normalized);
            return new LinqDocument(inferred, null, null, [], [], [], normalized, 0, false);
        }

        int headerEnd = -1;
        int closeIdx = normalized.IndexOf("</Query>", pos, StringComparison.Ordinal);
        int gtIdx = normalized.IndexOf('>', pos);
        if (gtIdx > pos && normalized[gtIdx - 1] == '/' && (closeIdx < 0 || gtIdx < closeIdx))
        {
            headerEnd = gtIdx + 1;
        }
        else if (closeIdx >= 0)
        {
            headerEnd = closeIdx + "</Query>".Length;
        }
        if (headerEnd < 0)
        {
            return new LinqDocument(LinqQueryKind.Statements, null, null, [], [], [], normalized, 0, false);
        }

        string headerXml = normalized[pos..headerEnd];
        int bodyStart = headerEnd;
        if (bodyStart < normalized.Length && normalized[bodyStart] == '\n')
        {
            bodyStart++;
        }
        int bodyStartLine = CountNewlines(normalized.AsSpan(0, bodyStart));
        string body = normalized[bodyStart..];

        try
        {
            XElement root = XElement.Parse(headerXml);
            LinqQueryKind kind = ParseKind((string?)root.Attribute("Kind"));
            XElement? connEl = root.Element("Connection");
            string? connId = (string?)connEl?.Element("ID") ?? (string?)root.Attribute("ConnectionId");
            string? connName = (string?)connEl?.Element("Name") ?? (string?)root.Attribute("ConnectionName");

            List<string> namespaces = [.. root.Elements("Namespace").Select(e => e.Value.Trim()).Where(v => v.Length > 0)];
            List<LinqPackageRef> packages = [];
            foreach (XElement nuget in root.Elements("NuGetReference"))
            {
                string pkgName = nuget.Value.Trim();
                if (pkgName.Length > 0)
                {
                    packages.Add(new LinqPackageRef(pkgName, (string?)nuget.Attribute("Version")));
                }
            }
            List<string> refs = [.. root.Elements("Reference").Select(e => e.Value.Trim()).Where(v => v.Length > 0)];

            return new LinqDocument(kind, connId, connName, namespaces, packages, refs, body, bodyStartLine, true, bodyStart);
        }
        catch (System.Xml.XmlException)
        {
            return new LinqDocument(LinqQueryKind.Statements, null, null, [], [], [], normalized, 0, false);
        }
    }

    public string Serialize()
    {
        XElement root = new("Query", new XAttribute("Kind", Kind.ToString()));
        if (!string.IsNullOrWhiteSpace(ConnectionId) || !string.IsNullOrWhiteSpace(ConnectionName))
        {
            XElement conn = new("Connection");
            if (!string.IsNullOrWhiteSpace(ConnectionId))
            {
                conn.Add(new XElement("ID", ConnectionId));
            }
            if (!string.IsNullOrWhiteSpace(ConnectionName))
            {
                conn.Add(new XElement("Name", ConnectionName));
            }
            root.Add(conn);
        }
        foreach (string ns in Namespaces)
        {
            root.Add(new XElement("Namespace", ns));
        }
        foreach (LinqPackageRef pkg in Packages)
        {
            XElement el = new("NuGetReference", pkg.Name);
            if (!string.IsNullOrWhiteSpace(pkg.Version))
            {
                el.SetAttributeValue("Version", pkg.Version);
            }
            root.Add(el);
        }
        foreach (string reference in References)
        {
            root.Add(new XElement("Reference", reference));
        }

        string xml = root.ToString(SaveOptions.None).Replace("\r\n", "\n");
        return $"{xml}\n\n{Body.TrimStart('\r', '\n')}";
    }

    private static LinqQueryKind InferKindWithoutHeader(string code)
    {
        string trimmed = code.Trim();
        if (trimmed.Length == 0)
        {
            return LinqQueryKind.Statements;
        }
        // Single expression (no newlines, no declaration/control keywords, at most one trailing ';').
        string withoutTrailingSemi = trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
        if (!withoutTrailingSemi.Contains('\n') && !withoutTrailingSemi.Contains(';'))
        {
            return LinqQueryKind.Expression;
        }
        return LinqQueryKind.Statements;
    }

    private static LinqQueryKind ParseKind(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "expression" => LinqQueryKind.Expression,
        "program" => LinqQueryKind.Program,
        _ => LinqQueryKind.Statements,
    };

    private static int CountNewlines(ReadOnlySpan<char> span)
    {
        int count = 0;
        foreach (char c in span)
        {
            if (c == '\n')
            {
                count++;
            }
        }
        return count;
    }
}

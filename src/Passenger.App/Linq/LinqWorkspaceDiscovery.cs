using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Linq;
using Passenger.Workspace;

namespace Passenger.App.Linq;

/// <summary>Discovered connection string from an <c>appsettings*.json</c> file in the workspace.</summary>
public sealed record DiscoveredConnectionString(string SourceFile, string Name, string ConnectionString, LinqDataSourceKind GuessedKind);

/// <summary>Discovered EF Core <c>DbContext</c> class in a workspace project.</summary>
public sealed record DiscoveredDbContext(string ProjectPath, string ProjectName, string FullTypeName);

/// <summary>Scans the open workspace for <c>appsettings*.json</c> <c>ConnectionStrings</c> and EF Core <c>DbContext</c> classes.</summary>
public static partial class LinqWorkspaceDiscovery
{
    public static IReadOnlyList<DiscoveredConnectionString> FindConnectionStrings(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        List<DiscoveredConnectionString> results = [];
        try
        {
            foreach (string file in EnumerateFilesBounded(folder, "appsettings*.json", maxDepth: 4, maxFiles: 40))
            {
                try
                {
                    using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using JsonDocument doc = JsonDocument.Parse(fs, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }
                    if (JsonMembers.Object(doc.RootElement, "ConnectionStrings") is not { } csSection)
                    {
                        continue;
                    }
                    string rel = Path.GetRelativePath(folder, file);
                    foreach (JsonProperty prop in csSection.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } cs)
                        {
                            results.Add(new DiscoveredConnectionString(rel, prop.Name, cs, GuessKind(cs)));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return results;
    }

    public static IReadOnlyList<DiscoveredDbContext> FindDbContexts(string? folder, IReadOnlyList<ProjectModel>? projects)
    {
        List<DiscoveredDbContext> results = [];
        if (projects is null || projects.Count == 0)
        {
            return results;
        }

        foreach (ProjectModel proj in projects)
        {
            if (proj.IsProgram || string.IsNullOrWhiteSpace(proj.Folder) || !Directory.Exists(proj.Folder))
            {
                continue;
            }
            string projName = Path.GetFileNameWithoutExtension(proj.Path);
            foreach (string csFile in EnumerateFilesBounded(proj.Folder, "*.cs", maxDepth: 5, maxFiles: 120))
            {
                try
                {
                    string text = File.ReadAllText(csFile);
                    if (!text.Contains("DbContext", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string? ns = null;
                    Match nsMatch = NamespaceRegex().Match(text);
                    if (nsMatch.Success)
                    {
                        ns = nsMatch.Groups[1].Value;
                    }
                    foreach (Match m in DbContextClassRegex().Matches(text))
                    {
                        string cls = m.Groups[1].Value;
                        string full = string.IsNullOrEmpty(ns) ? cls : $"{ns}.{cls}";
                        if (!results.Any(r => string.Equals(r.ProjectPath, proj.Path, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(r.FullTypeName, full, StringComparison.Ordinal)))
                        {
                            results.Add(new DiscoveredDbContext(proj.Path, projName, full));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return results;
    }

    public static LinqDataSourceKind GuessKind(string cs)
    {
        if (cs.Contains(".db", StringComparison.OrdinalIgnoreCase)
            || cs.Contains(".sqlite", StringComparison.OrdinalIgnoreCase)
            || (cs.Contains("Data Source=", StringComparison.OrdinalIgnoreCase) && !cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase) && !cs.Contains("Trusted_Connection=", StringComparison.OrdinalIgnoreCase) && !cs.Contains("Integrated Security=", StringComparison.OrdinalIgnoreCase)))
        {
            return LinqDataSourceKind.Sqlite;
        }
        if (cs.Contains("Host=", StringComparison.OrdinalIgnoreCase) || cs.Contains("Username=", StringComparison.OrdinalIgnoreCase))
        {
            return LinqDataSourceKind.PostgreSql;
        }
        if (cs.Contains("SslMode=", StringComparison.OrdinalIgnoreCase) || cs.Contains("Port=3306", StringComparison.OrdinalIgnoreCase) || cs.Contains("Uid=", StringComparison.OrdinalIgnoreCase))
        {
            return LinqDataSourceKind.MySql;
        }
        return LinqDataSourceKind.SqlServer;
    }

    private static IEnumerable<string> EnumerateFilesBounded(string root, string pattern, int maxDepth, int maxFiles)
    {
        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((root, 0));
        int count = 0;
        while (queue.Count > 0 && count < maxFiles)
        {
            (string dir, int depth) = queue.Dequeue();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (string f in files)
            {
                yield return f;
                count++;
                if (count >= maxFiles)
                {
                    yield break;
                }
            }
            if (depth >= maxDepth)
            {
                continue;
            }
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Multiline)]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]+>)?\s*:\s*(?:Identity)?DbContext\b")]
    private static partial Regex DbContextClassRegex();
}

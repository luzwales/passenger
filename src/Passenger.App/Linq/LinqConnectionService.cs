using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Linq;

namespace Passenger.App.Linq;

/// <summary>Persisted list of user-defined LINQ data connections (stored in <c>linqConnections.json</c>).</summary>
public sealed record LinqConnectionsState : IStateDocument<LinqConnectionsState>
{
    public static IReadOnlyList<Action<JsonObject>> Upgrades => [];

    public IReadOnlyList<LinqConnectionInfo> Connections { get; init; } = [];

    public LinqConnectionsState Clamped() =>
        new() { Connections = [.. (Connections ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name))] };
}

/// <summary>In-memory schema cache and code generator bridge for <see cref="LinqConnectionsState"/>.</summary>
public sealed class LinqConnectionService
{
    private readonly Dictionary<string, LinqDatabaseSchema> schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> generatedFiles = new(StringComparer.Ordinal);

    public IReadOnlyList<LinqConnectionInfo> Connections { get; private set; } = [];

    public event EventHandler? Changed;

    public void Load(LinqConnectionsState state)
    {
        Connections = state.Connections ?? [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public LinqConnectionsState ToState() => new() { Connections = Connections };

    public LinqConnectionInfo? Find(string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            return null;
        }
        return Connections.FirstOrDefault(c => string.Equals(c.Id, idOrName, StringComparison.OrdinalIgnoreCase))
            ?? Connections.FirstOrDefault(c => string.Equals(c.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public void AddOrUpdate(LinqConnectionInfo info)
    {
        List<LinqConnectionInfo> list = [.. Connections];
        int idx = list.FindIndex(c => string.Equals(c.Id, info.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            list[idx] = info;
        }
        else
        {
            list.Add(info);
        }
        Connections = list;
        schemas.Remove(info.Id);
        generatedFiles.Remove(info.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string id)
    {
        Connections = [.. Connections.Where(c => !string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))];
        schemas.Remove(id);
        generatedFiles.Remove(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public LinqDatabaseSchema? GetCachedSchema(string id) =>
        schemas.TryGetValue(id, out LinqDatabaseSchema? s) ? s : null;

    public async Task<LinqDatabaseSchema> ProbeAndGenerateAsync(LinqConnectionInfo info, string stateRoot, CancellationToken cancellationToken = default)
    {
        LinqDatabaseSchema schema = await LinqSchemaProber.ProbeAsync(info, cancellationToken);
        schemas[info.Id] = schema;
        string code = LinqContextGenerator.Generate(schema, info.Target);
        string dir = Path.Combine(stateRoot, "linq-contexts");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{info.Id}.g.cs");
        await Core.BackgroundWork.Run(() => Core.AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(code)), cancellationToken);
        generatedFiles[info.Id] = path;
        Changed?.Invoke(this, EventArgs.Empty);
        return schema;
    }

    public string? GetGeneratedContextPath(string id) =>
        generatedFiles.TryGetValue(id, out string? p) && File.Exists(p) ? p : null;
}

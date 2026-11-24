using System.Text.Json.Nodes;
using Passenger.Agent;
using Passenger.App.State;

namespace Passenger.App.Agent;

/// <summary>Persisted state document for Pi Agent provider endpoints, models, and sidebar preference.</summary>
public sealed record PiAgentState : IStateDocument<PiAgentState>
{
    public List<AgentProviderEndpoint> Providers { get; init; } = [];
    public string? ActiveProviderId { get; init; }
    public string? ActiveModelEntryId { get; init; }
    public List<AgentModelProfile> Models { get; init; } = [];
    public string? ActiveModelId { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 8192;
    public bool SidebarOpen { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [];

    public PiAgentConfig ToConfig() => new PiAgentConfig
    {
        Providers = Providers,
        ActiveProviderId = ActiveProviderId,
        ActiveModelEntryId = ActiveModelEntryId,
        Models = Models,
        ActiveModelId = ActiveModelId,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
    }.Clamped();

    public static PiAgentState FromConfig(PiAgentConfig config, bool sidebarOpen = false)
    {
        PiAgentConfig clean = config.Clamped();
        return new PiAgentState
        {
            Providers = [.. clean.Providers],
            ActiveProviderId = clean.ActiveProviderId,
            ActiveModelEntryId = clean.ActiveModelEntryId,
            Models = [.. clean.Models],
            ActiveModelId = clean.ActiveModelId,
            Temperature = clean.Temperature,
            MaxTokens = clean.MaxTokens,
            SidebarOpen = sidebarOpen,
        };
    }

    public PiAgentState Clamped() => FromConfig(ToConfig(), SidebarOpen);
}

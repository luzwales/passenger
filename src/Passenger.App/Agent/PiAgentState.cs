using System.Text.Json.Nodes;
using Passenger.Agent;
using Passenger.App.State;

namespace Passenger.App.Agent;

/// <summary>Persisted state document for Pi Agent model profiles and sampling settings.</summary>
public sealed record PiAgentState : IStateDocument<PiAgentState>
{
    public List<AgentModelProfile> Models { get; init; } = [];
    public string? ActiveModelId { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 8192;

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [];

    public PiAgentConfig ToConfig() => new PiAgentConfig
    {
        Models = Models,
        ActiveModelId = ActiveModelId,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
    }.Clamped();

    public static PiAgentState FromConfig(PiAgentConfig config)
    {
        PiAgentConfig clean = config.Clamped();
        return new PiAgentState
        {
            Models = [.. clean.Models],
            ActiveModelId = clean.ActiveModelId,
            Temperature = clean.Temperature,
            MaxTokens = clean.MaxTokens,
        };
    }

    public PiAgentState Clamped() => FromConfig(ToConfig());
}

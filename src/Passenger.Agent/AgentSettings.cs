namespace Passenger.Agent;

/// <summary>Configuration for one LLM endpoint, API key, model, and protocol.</summary>
public sealed record AgentModelProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AgentProviderKind Provider { get; init; } = AgentProviderKind.OpenAI;
    public string BaseUrl { get; init; } = AgentProviderPreset.OpenAI.BaseUrl;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = AgentProviderPreset.OpenAI.Model;
    public AgentProtocol Protocol { get; init; } = AgentProtocol.OpenAiResponses;
    public long? ContextWindow { get; init; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name.Trim();
            }
            string model = Model.Trim();
            return model.Length > 0 ? model : AgentProviderPreset.For(Provider).Name;
        }
    }

    public static string NewId() => "m" + Guid.NewGuid().ToString("N")[..8];

    public static AgentModelProfile Create(AgentProviderKind kind)
    {
        AgentProviderPreset preset = AgentProviderPreset.For(kind);
        return new AgentModelProfile
        {
            Id = NewId(),
            Name = preset.Name,
            Provider = kind,
            BaseUrl = preset.BaseUrl,
            Model = preset.Model,
            Protocol = preset.Protocol,
        };
    }

    public AgentModelProfile Clamped()
    {
        AgentProviderKind provider = Enum.IsDefined(Provider) ? Provider : AgentProviderKind.Custom;
        AgentProviderPreset preset = AgentProviderPreset.For(provider);
        AgentProtocol protocol = Enum.IsDefined(Protocol) ? Protocol : preset.Protocol;
        string id = string.IsNullOrWhiteSpace(Id) ? NewId() : Id.Trim();
        string baseUrl = (BaseUrl ?? string.Empty).Trim();
        string model = (Model ?? string.Empty).Trim();
        return this with
        {
            Id = id,
            Name = (Name ?? string.Empty).Trim(),
            Provider = provider,
            BaseUrl = baseUrl.Length > 0 ? baseUrl : preset.BaseUrl,
            ApiKey = (ApiKey ?? string.Empty).Trim(),
            Model = model.Length > 0 ? model : preset.Model,
            Protocol = protocol,
            ContextWindow = ContextWindow is > 0 ? ContextWindow : null,
        };
    }
}

/// <summary>Persisted model profiles and sampling defaults for the Pi agent.</summary>
public sealed record PiAgentConfig
{
    public IReadOnlyList<AgentModelProfile> Models { get; init; } = [];
    public string? ActiveModelId { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 8192;

    public AgentModelProfile? ActiveModel =>
        Models.FirstOrDefault(m => string.Equals(m.Id, ActiveModelId, StringComparison.Ordinal))
        ?? (Models.Count > 0 ? Models[0] : null);

    public PiAgentConfig Clamped()
    {
        List<AgentModelProfile> list = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AgentModelProfile raw in Models ?? [])
        {
            if (raw is null)
            {
                continue;
            }
            AgentModelProfile normalized = raw.Clamped();
            if (!seen.Add(normalized.Id))
            {
                normalized = normalized with { Id = AgentModelProfile.NewId() };
                seen.Add(normalized.Id);
            }
            list.Add(normalized);
        }
        if (list.Count == 0)
        {
            list =
            [
                AgentModelProfile.Create(AgentProviderKind.OpenAI),
                AgentModelProfile.Create(AgentProviderKind.Anthropic),
                AgentModelProfile.Create(AgentProviderKind.DeepSeek),
            ];
        }
        string activeId = list.Any(m => string.Equals(m.Id, ActiveModelId, StringComparison.Ordinal))
            ? ActiveModelId!
            : list[0].Id;
        double temp = double.IsFinite(Temperature) ? Math.Clamp(Temperature, 0.0, 2.0) : 0.2;
        int maxTokens = Math.Clamp(MaxTokens, 256, 128_000);
        return new PiAgentConfig
        {
            Models = list,
            ActiveModelId = activeId,
            Temperature = temp,
            MaxTokens = maxTokens,
        };
    }
}

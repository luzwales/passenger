namespace Passenger.Agent;

/// <summary>Configuration for a single model under a provider endpoint, with its own thinking level and token caps.</summary>
public sealed record AgentModelEntry
{
    public string Id { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AgentThinkingLevel ThinkingLevel { get; init; } = AgentThinkingLevel.Off;
    public int MaxTokens { get; init; } = 8192;
    public long ContextWindow { get; init; } = 128_000;
    public bool SupportsImages { get; init; } = true;

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : ModelId.Trim();

    public static string NewId() => "mdl" + Guid.NewGuid().ToString("N")[..8];

    public static AgentModelEntry Create(string modelId, string? name = null, AgentThinkingLevel thinking = AgentThinkingLevel.Off, int maxTokens = 8192, long contextWindow = 128_000) =>
        new()
        {
            Id = NewId(),
            ModelId = modelId.Trim(),
            Name = (name ?? string.Empty).Trim(),
            ThinkingLevel = thinking,
            MaxTokens = Math.Clamp(maxTokens, 256, 128_000),
            ContextWindow = Math.Clamp(contextWindow, 1024, 4_000_000),
            SupportsImages = true,
        };

    public AgentModelEntry Clamped(string fallbackModelId = "gpt-4.1")
    {
        string modelId = (ModelId ?? string.Empty).Trim();
        AgentThinkingLevel thinking = Enum.IsDefined(ThinkingLevel) ? ThinkingLevel : AgentThinkingLevel.Off;
        return this with
        {
            Id = string.IsNullOrWhiteSpace(Id) ? NewId() : Id.Trim(),
            ModelId = modelId.Length > 0 ? modelId : fallbackModelId,
            Name = (Name ?? string.Empty).Trim(),
            ThinkingLevel = thinking,
            MaxTokens = Math.Clamp(MaxTokens, 256, 128_000),
            ContextWindow = ContextWindow <= 0 ? 128_000 : Math.Clamp(ContextWindow, 1024, 4_000_000),
        };
    }
}

/// <summary>One LLM endpoint (base URL, API key, wire protocol) hosting one or more configured models.</summary>
public sealed record AgentProviderEndpoint
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AgentProviderKind Provider { get; init; } = AgentProviderKind.OpenAI;
    public AgentProtocol Protocol { get; init; } = AgentProtocol.OpenAiResponses;
    public string BaseUrl { get; init; } = AgentProviderPreset.OpenAI.BaseUrl;
    public string ApiKey { get; init; } = string.Empty;
    public IReadOnlyList<AgentModelEntry> Models { get; init; } = [];

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name.Trim() : AgentProviderPreset.For(Provider).Name;

    public static string NewId() => "prv" + Guid.NewGuid().ToString("N")[..8];

    public static AgentProviderEndpoint Create(AgentProviderKind kind)
    {
        AgentProviderPreset preset = AgentProviderPreset.For(kind);
        return new AgentProviderEndpoint
        {
            Id = NewId(),
            Name = preset.Name,
            Provider = kind,
            Protocol = preset.Protocol,
            BaseUrl = preset.BaseUrl,
            Models = [AgentModelEntry.Create(preset.Model)],
        };
    }

    public AgentProviderEndpoint Clamped()
    {
        AgentProviderKind provider = Enum.IsDefined(Provider) ? Provider : AgentProviderKind.Custom;
        AgentProviderPreset preset = AgentProviderPreset.For(provider);
        AgentProtocol protocol = Enum.IsDefined(Protocol) ? Protocol : preset.Protocol;
        string baseUrl = (BaseUrl ?? string.Empty).Trim();

        List<AgentModelEntry> models = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AgentModelEntry raw in Models ?? [])
        {
            if (raw is null)
            {
                continue;
            }
            AgentModelEntry clean = raw.Clamped(preset.Model);
            if (!seen.Add(clean.Id))
            {
                clean = clean with { Id = AgentModelEntry.NewId() };
                seen.Add(clean.Id);
            }
            models.Add(clean);
        }
        if (models.Count == 0)
        {
            models.Add(AgentModelEntry.Create(preset.Model));
        }
        return this with
        {
            Id = string.IsNullOrWhiteSpace(Id) ? NewId() : Id.Trim(),
            Name = (Name ?? string.Empty).Trim(),
            Provider = provider,
            Protocol = protocol,
            BaseUrl = baseUrl.Length > 0 ? baseUrl : preset.BaseUrl,
            ApiKey = (ApiKey ?? string.Empty).Trim(),
            Models = models,
        };
    }
}

/// <summary>Flattened active endpoint + model selection resolved from <see cref="PiAgentConfig"/>.</summary>
public sealed record AgentModelProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public AgentProviderKind Provider { get; init; } = AgentProviderKind.OpenAI;
    public string BaseUrl { get; init; } = AgentProviderPreset.OpenAI.BaseUrl;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = AgentProviderPreset.OpenAI.Model;
    public AgentProtocol Protocol { get; init; } = AgentProtocol.OpenAiResponses;
    public AgentThinkingLevel ThinkingLevel { get; init; } = AgentThinkingLevel.Off;
    public int MaxTokens { get; init; } = 8192;
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
        AgentThinkingLevel thinking = Enum.IsDefined(ThinkingLevel) ? ThinkingLevel : AgentThinkingLevel.Off;
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
            ThinkingLevel = thinking,
            MaxTokens = Math.Clamp(MaxTokens, 256, 128_000),
            ContextWindow = ContextWindow is > 0 ? ContextWindow : 128_000,
        };
    }
}

/// <summary>Persisted endpoints, models, and sampling defaults for the Pi agent.</summary>
public sealed record PiAgentConfig
{
    public IReadOnlyList<AgentProviderEndpoint> Providers { get; init; } = [];
    public string? ActiveProviderId { get; init; }
    public string? ActiveModelEntryId { get; init; }
    public IReadOnlyList<AgentModelProfile> Models { get; init; } = [];
    public string? ActiveModelId { get; init; }
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 8192;

    public AgentProviderEndpoint? ActiveProvider =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, ActiveProviderId, StringComparison.Ordinal))
        ?? (Providers.Count > 0 ? Providers[0] : null);

    public AgentModelEntry? ActiveModelEntry =>
        ActiveProvider is { } provider
            ? provider.Models.FirstOrDefault(m => string.Equals(m.Id, ActiveModelEntryId, StringComparison.Ordinal))
                ?? (provider.Models.Count > 0 ? provider.Models[0] : null)
            : null;

    public AgentModelProfile? ActiveModel
    {
        get
        {
            if (ActiveProvider is { } provider && ActiveModelEntry is { } model)
            {
                return new AgentModelProfile
                {
                    Id = model.Id,
                    Name = model.DisplayName,
                    Provider = provider.Provider,
                    BaseUrl = provider.BaseUrl,
                    ApiKey = provider.ApiKey,
                    Model = model.ModelId,
                    Protocol = provider.Protocol,
                    ThinkingLevel = model.ThinkingLevel,
                    MaxTokens = model.MaxTokens,
                    ContextWindow = model.ContextWindow,
                };
            }
            return Models.FirstOrDefault(m => string.Equals(m.Id, ActiveModelId, StringComparison.Ordinal))
                ?? (Models.Count > 0 ? Models[0] : null);
        }
    }

    public PiAgentConfig Clamped()
    {
        double temp = double.IsFinite(Temperature) ? Math.Clamp(Temperature, 0.0, 2.0) : 0.2;
        int fallbackMaxTokens = Math.Clamp(MaxTokens, 256, 128_000);

        List<AgentProviderEndpoint> providers = [];
        HashSet<string> seenProviders = new(StringComparer.Ordinal);
        foreach (AgentProviderEndpoint raw in Providers ?? [])
        {
            if (raw is null)
            {
                continue;
            }
            AgentProviderEndpoint clean = raw.Clamped();
            if (!seenProviders.Add(clean.Id))
            {
                clean = clean with { Id = AgentProviderEndpoint.NewId() };
                seenProviders.Add(clean.Id);
            }
            providers.Add(clean);
        }

        if (providers.Count == 0 && Models is { Count: > 0 })
        {
            providers = MigrateLegacyModels(Models, fallbackMaxTokens);
        }

        if (providers.Count == 0)
        {
            providers =
            [
                AgentProviderEndpoint.Create(AgentProviderKind.OpenAI),
                AgentProviderEndpoint.Create(AgentProviderKind.Anthropic),
                AgentProviderEndpoint.Create(AgentProviderKind.DeepSeek),
            ];
        }

        string activeProviderId = ActiveProviderId ?? string.Empty;
        AgentProviderEndpoint? chosenProvider = providers.FirstOrDefault(p => string.Equals(p.Id, activeProviderId, StringComparison.Ordinal));
        if (chosenProvider is null && !string.IsNullOrWhiteSpace(ActiveModelId))
        {
            chosenProvider = providers.FirstOrDefault(p => p.Models.Any(m => string.Equals(m.Id, ActiveModelId, StringComparison.Ordinal)));
        }
        chosenProvider ??= providers[0];

        string desiredModelId = ActiveModelEntryId ?? ActiveModelId ?? string.Empty;
        AgentModelEntry chosenModel = chosenProvider.Models.FirstOrDefault(m => string.Equals(m.Id, desiredModelId, StringComparison.Ordinal))
            ?? chosenProvider.Models[0];

        List<AgentModelProfile> flattened =
        [
            .. from p in providers
               from m in p.Models
               select new AgentModelProfile
               {
                   Id = m.Id,
                   Name = $"{p.DisplayName} · {m.DisplayName}",
                   Provider = p.Provider,
                   BaseUrl = p.BaseUrl,
                   ApiKey = p.ApiKey,
                   Model = m.ModelId,
                   Protocol = p.Protocol,
                   ThinkingLevel = m.ThinkingLevel,
                   MaxTokens = m.MaxTokens,
                   ContextWindow = m.ContextWindow,
               },
        ];

        return new PiAgentConfig
        {
            Providers = providers,
            ActiveProviderId = chosenProvider.Id,
            ActiveModelEntryId = chosenModel.Id,
            Models = flattened,
            ActiveModelId = chosenModel.Id,
            Temperature = temp,
            MaxTokens = chosenModel.MaxTokens,
        };
    }

    private static List<AgentProviderEndpoint> MigrateLegacyModels(IReadOnlyList<AgentModelProfile> legacy, int fallbackMaxTokens)
    {
        List<AgentProviderEndpoint> migrated = [];
        foreach (AgentModelProfile raw in legacy)
        {
            if (raw is null)
            {
                continue;
            }
            AgentModelProfile clean = raw.Clamped();
            AgentModelEntry entry = new()
            {
                Id = clean.Id,
                ModelId = clean.Model,
                Name = clean.DisplayName,
                ThinkingLevel = clean.ThinkingLevel,
                MaxTokens = clean.MaxTokens > 0 ? clean.MaxTokens : fallbackMaxTokens,
                ContextWindow = clean.ContextWindow ?? 128_000,
                SupportsImages = true,
            };

            int existingIndex = migrated.FindIndex(p =>
                p.Protocol == clean.Protocol
                && string.Equals(p.BaseUrl, clean.BaseUrl, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.ApiKey, clean.ApiKey, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                AgentProviderEndpoint current = migrated[existingIndex];
                migrated[existingIndex] = current with { Models = [.. current.Models, entry] };
            }
            else
            {
                migrated.Add(new AgentProviderEndpoint
                {
                    Id = AgentProviderEndpoint.NewId(),
                    Name = !string.IsNullOrWhiteSpace(clean.Name) ? clean.Name : AgentProviderPreset.For(clean.Provider).Name,
                    Provider = clean.Provider,
                    Protocol = clean.Protocol,
                    BaseUrl = clean.BaseUrl,
                    ApiKey = clean.ApiKey,
                    Models = [entry],
                });
            }
        }
        return migrated;
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;

namespace Passenger.Agent;

/// <summary>Reads and writes shared Pi CLI provider/model configuration under ~/.pi/agent/models.json and settings.json.</summary>
public static class PiAgentModelsSync
{
    public static string DefaultAgentDir() => Config.GetAgentDir();

    public static PiAgentConfig LoadSharedConfig(PiAgentConfig current, string? agentDir = null)
    {
        string dir = string.IsNullOrWhiteSpace(agentDir) ? DefaultAgentDir() : agentDir;
        string modelsPath = Path.Combine(dir, "models.json");
        if (!File.Exists(modelsPath))
        {
            return current.Clamped();
        }

        try
        {
            string raw = File.ReadAllText(modelsPath, Encoding.UTF8);
            if (JsonNode.Parse(raw) is not JsonObject root || root["providers"] is not JsonObject providersNode)
            {
                return current.Clamped();
            }

            SettingsManager settings = SettingsManager.Create(dir, dir);
            string? defaultProvider = settings.GetDefaultProvider();
            string? defaultModel = settings.GetDefaultModel();
            AgentThinkingLevel defaultThinking = AgentThinkingCatalog.Parse(settings.GetDefaultThinkingLevel());

            List<AgentProviderEndpoint> merged = [.. current.Clamped().Providers];
            string? matchedProviderId = current.ActiveProviderId;
            string? matchedModelId = current.ActiveModelEntryId;

            foreach ((string key, JsonNode? value) in providersNode)
            {
                if (value is not JsonObject providerObj)
                {
                    continue;
                }
                string baseUrl = ReadString(providerObj, "baseUrl") ?? string.Empty;
                string apiKey = ReadString(providerObj, "apiKey") ?? string.Empty;
                string api = ReadString(providerObj, "api") ?? "openai-completions";
                AgentProtocol protocol = AgentProtocolCatalog.FromApiId(api);
                AgentProviderKind kind = InferKind(key, baseUrl, protocol);

                List<AgentModelEntry> models = [];
                if (providerObj["models"] is JsonArray modelArray)
                {
                    foreach (JsonNode? item in modelArray)
                    {
                        if (item is not JsonObject modelObj || ReadString(modelObj, "id") is not { Length: > 0 } modelId)
                        {
                            continue;
                        }
                        string? modelName = ReadString(modelObj, "name");
                        int maxTokens = (int)Math.Clamp(ReadDouble(modelObj, "maxTokens") ?? 8192, 256, 128_000);
                        long contextWindow = (long)Math.Clamp(ReadDouble(modelObj, "contextWindow") ?? 128_000, 1024, 4_000_000);
                        string? perModelThinking = settings.GetModelThinkingLevel(key, modelId);
                        AgentThinkingLevel thinking = !string.IsNullOrWhiteSpace(perModelThinking)
                            ? AgentThinkingCatalog.Parse(perModelThinking)
                            : defaultThinking;
                        models.Add(AgentModelEntry.Create(modelId, modelName, thinking, maxTokens, contextWindow));
                    }
                }
                if (models.Count == 0)
                {
                    continue;
                }

                int existingIdx = merged.FindIndex(p => string.Equals(p.DisplayName, key, StringComparison.OrdinalIgnoreCase)
                    || (baseUrl.Length > 0 && string.Equals(p.BaseUrl.TrimEnd('/'), baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) && p.Protocol == protocol));

                AgentProviderEndpoint endpoint;
                if (existingIdx >= 0)
                {
                    AgentProviderEndpoint existing = merged[existingIdx];
                    List<AgentModelEntry> combinedModels = [.. existing.Models];
                    foreach (AgentModelEntry sharedModel in models)
                    {
                        int mIdx = combinedModels.FindIndex(m => string.Equals(m.ModelId, sharedModel.ModelId, StringComparison.Ordinal));
                        if (mIdx < 0)
                        {
                            combinedModels.Add(sharedModel);
                        }
                    }
                    endpoint = (existing with
                    {
                        Name = string.IsNullOrWhiteSpace(existing.Name) ? key : existing.Name,
                        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? existing.BaseUrl : baseUrl,
                        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? existing.ApiKey : apiKey,
                        Protocol = protocol,
                        Models = combinedModels,
                    }).Clamped();
                    merged[existingIdx] = endpoint;
                }
                else
                {
                    endpoint = new AgentProviderEndpoint
                    {
                        Id = AgentProviderEndpoint.NewId(),
                        Name = key,
                        Provider = kind,
                        Protocol = protocol,
                        BaseUrl = baseUrl,
                        ApiKey = apiKey,
                        Models = models,
                    }.Clamped();
                    merged.Add(endpoint);
                }

                if (string.Equals(key, defaultProvider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(defaultModel))
                {
                    AgentModelEntry? target = endpoint.Models.FirstOrDefault(m => string.Equals(m.ModelId, defaultModel, StringComparison.Ordinal));
                    if (target is not null && string.IsNullOrWhiteSpace(current.ActiveModelEntryId))
                    {
                        matchedProviderId = endpoint.Id;
                        matchedModelId = target.Id;
                    }
                }
            }

            return (current with
            {
                Providers = merged,
                ActiveProviderId = matchedProviderId,
                ActiveModelEntryId = matchedModelId,
            }).Clamped();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            PassengerLog.Report(error);
            return current.Clamped();
        }
    }

    public static void SaveSharedConfig(PiAgentConfig config, string? agentDir = null)
    {
        PiAgentConfig clean = config.Clamped();
        string dir = string.IsNullOrWhiteSpace(agentDir) ? DefaultAgentDir() : agentDir;
        try
        {
            Directory.CreateDirectory(dir);
            string modelsPath = Path.Combine(dir, "models.json");

            JsonObject root = ReadOrCreateObject(modelsPath);
            JsonObject providersObj = root["providers"] as JsonObject ?? [];
            root["providers"] = providersObj;

            HashSet<string> usedKeys = new(StringComparer.Ordinal);
            foreach (AgentProviderEndpoint endpoint in clean.Providers)
            {
                string key = SanitizeKey(endpoint.DisplayName, usedKeys);
                JsonObject providerObj = providersObj[key] as JsonObject ?? [];
                providerObj["baseUrl"] = endpoint.BaseUrl;
                if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
                {
                    providerObj["apiKey"] = endpoint.ApiKey;
                }
                providerObj["api"] = AgentProtocolCatalog.ApiId(endpoint.Protocol);

                JsonArray modelsArr = [];
                foreach (AgentModelEntry model in endpoint.Models)
                {
                    JsonObject modelObj = new()
                    {
                        ["id"] = model.ModelId,
                        ["name"] = model.DisplayName,
                        ["reasoning"] = model.ThinkingLevel != AgentThinkingLevel.Off,
                        ["input"] = new JsonArray("text", "image"),
                        ["contextWindow"] = model.ContextWindow,
                        ["maxTokens"] = model.MaxTokens,
                    };
                    modelsArr.Add(modelObj);
                }
                providerObj["models"] = modelsArr;
                providersObj[key] = providerObj;
            }

            string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            AtomicFile.Write(modelsPath, Encoding.UTF8.GetBytes(json));

            if (clean.ActiveProvider is { } activeProvider && clean.ActiveModelEntry is { } activeModel)
            {
                string activeKey = SanitizeKey(activeProvider.DisplayName, []);
                SettingsManager settings = SettingsManager.Create(dir, dir);
                settings.SetDefaultModelAndProvider(activeKey, activeModel.ModelId);
                settings.SetDefaultThinkingLevel(AgentThinkingCatalog.ToPi(activeModel.ThinkingLevel));
                foreach (AgentProviderEndpoint p in clean.Providers)
                {
                    string pKey = SanitizeKey(p.DisplayName, []);
                    foreach (AgentModelEntry m in p.Models)
                    {
                        settings.SetModelThinkingLevel(pKey, m.ModelId, AgentThinkingCatalog.ToPi(m.ThinkingLevel));
                    }
                }
                // Flush settings.json synchronously so callers writing config observe changes immediately.
                Blocking.Until(settings.FlushAsync());
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            PassengerLog.Report(error);
        }
    }

    private static JsonObject ReadOrCreateObject(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        try
        {
            string raw = File.ReadAllText(path, Encoding.UTF8);
            return JsonNode.Parse(raw) as JsonObject ?? [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            PassengerLog.Report(error);
            return [];
        }
    }

    private static string SanitizeKey(string raw, HashSet<string> used)
    {
        string trimmed = string.IsNullOrWhiteSpace(raw) ? "custom" : raw.Trim();
        string candidate = trimmed;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{trimmed}-{suffix++}";
        }
        return candidate;
    }

    private static AgentProviderKind InferKind(string key, string baseUrl, AgentProtocol protocol)
    {
        if (key.Contains("anthropic", StringComparison.OrdinalIgnoreCase) || protocol == AgentProtocol.AnthropicMessages)
        {
            return AgentProviderKind.Anthropic;
        }
        if (key.Contains("deepseek", StringComparison.OrdinalIgnoreCase) || baseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
        {
            return AgentProviderKind.DeepSeek;
        }
        if (key.Contains("openai", StringComparison.OrdinalIgnoreCase) || baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            return AgentProviderKind.OpenAI;
        }
        return AgentProviderKind.Custom;
    }

    private static string? ReadString(JsonObject owner, string key) =>
        owner[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static double? ReadDouble(JsonObject owner, string key) =>
        owner[key] is JsonValue value && value.TryGetValue(out double number) ? number : null;
}

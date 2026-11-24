using Pi.Ai.Types;

namespace Passenger.Agent;

/// <summary>Model wire protocol supported by the Pi agent runtime.</summary>
public enum AgentProtocol
{
    OpenAiCompatible,
    OpenAiResponses,
    AnthropicMessages,
}

/// <summary>Provider preset kind for quick defaults in endpoint setup.</summary>
public enum AgentProviderKind
{
    OpenAI,
    Anthropic,
    DeepSeek,
    Custom,
}

/// <summary>Display and Pi.Ai API identifier metadata for a protocol.</summary>
public sealed record AgentProtocolOption(AgentProtocol Kind, string Name, string Description, string ApiId);

/// <summary>Catalog of supported LLM wire protocols.</summary>
public static class AgentProtocolCatalog
{
    public static readonly AgentProtocolOption OpenAiCompatible = new(
        AgentProtocol.OpenAiCompatible,
        "OpenAI Compatible",
        "POST /chat/completions — OpenAI, DeepSeek, vLLM, Ollama, and compatible gateways",
        "openai-completions");

    public static readonly AgentProtocolOption OpenAiResponses = new(
        AgentProtocol.OpenAiResponses,
        "OpenAI Responses",
        "POST /responses — OpenAI Responses API with reasoning support",
        "openai-responses");

    public static readonly AgentProtocolOption AnthropicMessages = new(
        AgentProtocol.AnthropicMessages,
        "Anthropic Messages",
        "POST /v1/messages — Anthropic Claude Messages API",
        "anthropic-messages");

    public static readonly IReadOnlyList<AgentProtocolOption> All = [OpenAiCompatible, OpenAiResponses, AnthropicMessages];

    public static AgentProtocolOption For(AgentProtocol kind) =>
        All.FirstOrDefault(option => option.Kind == kind) ?? OpenAiCompatible;

    public static AgentProtocol FromApiId(string? apiId) => apiId?.Trim().ToLowerInvariant() switch
    {
        "anthropic-messages" => AgentProtocol.AnthropicMessages,
        "openai-responses" => AgentProtocol.OpenAiResponses,
        _ => AgentProtocol.OpenAiCompatible,
    };

    public static string ShortLabel(AgentProtocol kind) => kind switch
    {
        AgentProtocol.OpenAiResponses => "Responses",
        AgentProtocol.AnthropicMessages => "Anthropic",
        _ => "OpenAI Compat",
    };

    public static string ApiId(AgentProtocol kind) => For(kind).ApiId;
}

/// <summary>Supported thinking/reasoning levels for a Pi Agent model.</summary>
public enum AgentThinkingLevel
{
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>Display and Pi.Ai mapping helpers for per-model thinking levels.</summary>
public static class AgentThinkingCatalog
{
    public static readonly IReadOnlyList<AgentThinkingLevel> All =
    [
        AgentThinkingLevel.Off,
        AgentThinkingLevel.Minimal,
        AgentThinkingLevel.Low,
        AgentThinkingLevel.Medium,
        AgentThinkingLevel.High,
        AgentThinkingLevel.XHigh,
        AgentThinkingLevel.Max,
    ];

    public static string Label(AgentThinkingLevel level) => level switch
    {
        AgentThinkingLevel.Off => "Off",
        AgentThinkingLevel.Minimal => "Minimal",
        AgentThinkingLevel.Low => "Low",
        AgentThinkingLevel.Medium => "Medium",
        AgentThinkingLevel.High => "High",
        AgentThinkingLevel.XHigh => "XHigh",
        AgentThinkingLevel.Max => "Max",
        _ => "Off",
    };

    public static string WireName(AgentThinkingLevel level) => level switch
    {
        AgentThinkingLevel.Off => "off",
        AgentThinkingLevel.Minimal => "minimal",
        AgentThinkingLevel.Low => "low",
        AgentThinkingLevel.Medium => "medium",
        AgentThinkingLevel.High => "high",
        AgentThinkingLevel.XHigh => "xhigh",
        AgentThinkingLevel.Max => "max",
        _ => "off",
    };

    public static ThinkingLevel ToPi(AgentThinkingLevel level) => level switch
    {
        AgentThinkingLevel.Minimal => ThinkingLevel.Minimal,
        AgentThinkingLevel.Low => ThinkingLevel.Low,
        AgentThinkingLevel.Medium => ThinkingLevel.Medium,
        AgentThinkingLevel.High => ThinkingLevel.High,
        AgentThinkingLevel.XHigh => ThinkingLevel.XHigh,
        AgentThinkingLevel.Max => ThinkingLevel.Max,
        _ => ThinkingLevel.Off,
    };

    public static AgentThinkingLevel FromPi(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => AgentThinkingLevel.Minimal,
        ThinkingLevel.Low => AgentThinkingLevel.Low,
        ThinkingLevel.Medium => AgentThinkingLevel.Medium,
        ThinkingLevel.High => AgentThinkingLevel.High,
        ThinkingLevel.XHigh => AgentThinkingLevel.XHigh,
        ThinkingLevel.Max => AgentThinkingLevel.Max,
        _ => AgentThinkingLevel.Off,
    };

    public static AgentThinkingLevel Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "minimal" => AgentThinkingLevel.Minimal,
        "low" => AgentThinkingLevel.Low,
        "medium" => AgentThinkingLevel.Medium,
        "high" => AgentThinkingLevel.High,
        "xhigh" => AgentThinkingLevel.XHigh,
        "max" => AgentThinkingLevel.Max,
        _ => AgentThinkingLevel.Off,
    };
}

/// <summary>Default endpoint and model presets for common providers.</summary>
public sealed record AgentProviderPreset(AgentProviderKind Kind, string Name, string BaseUrl, string Model, AgentProtocol Protocol)
{
    public static readonly AgentProviderPreset OpenAI = new(
        AgentProviderKind.OpenAI,
        "OpenAI",
        "https://api.openai.com/v1",
        "gpt-4.1",
        AgentProtocol.OpenAiResponses);

    public static readonly AgentProviderPreset Anthropic = new(
        AgentProviderKind.Anthropic,
        "Anthropic",
        "https://api.anthropic.com",
        "claude-sonnet-4-20250514",
        AgentProtocol.AnthropicMessages);

    public static readonly AgentProviderPreset DeepSeek = new(
        AgentProviderKind.DeepSeek,
        "DeepSeek",
        "https://api.deepseek.com/v1",
        "deepseek-chat",
        AgentProtocol.OpenAiCompatible);

    public static readonly AgentProviderPreset Custom = new(
        AgentProviderKind.Custom,
        "Custom",
        "http://localhost:11434/v1",
        "qwen2.5-coder",
        AgentProtocol.OpenAiCompatible);

    public static readonly IReadOnlyList<AgentProviderPreset> All = [OpenAI, Anthropic, DeepSeek, Custom];

    public static AgentProviderPreset For(AgentProviderKind kind) =>
        All.FirstOrDefault(preset => preset.Kind == kind) ?? Custom;
}

public enum AgentAttachmentKind
{
    File,
    Folder,
    Image,
}

/// <summary>A file, folder, or image attached to a prompt.</summary>
public sealed record AgentAttachment(
    string Path,
    string DisplayPath,
    AgentAttachmentKind Kind,
    long Size,
    int LineCount = 0,
    bool Truncated = false,
    string? Text = null,
    string? Error = null,
    string? MimeType = null)
{
    public bool HasText => !string.IsNullOrEmpty(Text);
}

public sealed record AgentHistoryTurn(string Role, string Text)
{
    public static AgentHistoryTurn User(string text) => new("user", text);

    public static AgentHistoryTurn Assistant(string text) => new("assistant", text);
}

public sealed record AgentRunRequest(
    string SessionId,
    string Prompt,
    string WorkspacePath,
    IReadOnlyList<AgentHistoryTurn> History,
    IReadOnlyList<AgentAttachment>? Images = null,
    string? SessionFile = null);

public enum AgentStopReason
{
    Completed,
    Aborted,
    Length,
    Error,
}

/// <summary>Streaming event emitted during an agent turn.</summary>
public abstract record AgentStreamEvent
{
    private AgentStreamEvent()
    {
    }

    public sealed record RunStarted(string SessionId, string Model, string? SessionFile = null) : AgentStreamEvent;

    public sealed record TextDelta(string Delta) : AgentStreamEvent;

    public sealed record ThinkingDelta(string Delta) : AgentStreamEvent;

    public sealed record ToolStarted(string CallId, string Name, string Label, string Arguments) : AgentStreamEvent;

    public sealed record ToolFinished(string CallId, string Name, string? Result, bool IsError, long? DurationMs) : AgentStreamEvent;

    public sealed record RunCompleted(AgentStopReason StopReason, string? Error, long InputTokens, long OutputTokens, string? Model, string? SessionFile = null) : AgentStreamEvent;

    public sealed record RunFailed(string Message) : AgentStreamEvent;
}

/// <summary>Result of testing connectivity for a model profile.</summary>
public sealed record ConnectionTestResult(bool Ok, string Title, string Detail, long ElapsedMs)
{
    public string Summary => Ok ? $"{Title} · {ElapsedMs} ms" : $"{Title}: {Detail}";

    public static ConnectionTestResult Success(string model, long elapsedMs) =>
        new(true, $"Connected ({model})", string.Empty, elapsedMs);

    public static ConnectionTestResult Failure(string title, string detail, long elapsedMs) =>
        new(false, title, detail, elapsedMs);
}

public sealed record AgentToolInfo(string Name, string Label, string Description, bool IsMutating);

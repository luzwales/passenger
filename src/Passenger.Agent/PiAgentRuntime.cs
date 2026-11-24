using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Passenger.Core;
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Passenger.Agent;

/// <summary>Runs Pi.CodingAgent turns over the open workspace with multi-protocol LLM switching.</summary>
public sealed class PiAgentRuntime
{
    private const string ProviderId = "passenger-pi";
    private const long MaxResponseBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, SessionHolder> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim sessionGate = new(1, 1);

    public Task<ConnectionTestResult> TestConnectionAsync(AgentModelProfile profile, CancellationToken cancel = default) =>
        ConnectionProbe.TestAsync(profile, cancel);

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        AgentRunRequest request,
        PiAgentConfig config,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        PiAgentConfig cleanConfig = config.Clamped();
        AgentModelProfile? profile = cleanConfig.ActiveModel;
        if (profile is null || string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            yield return new AgentStreamEvent.RunFailed("Configure an API Key for the selected model profile first.");
            yield break;
        }
        if (string.IsNullOrWhiteSpace(profile.Model))
        {
            yield return new AgentStreamEvent.RunFailed("Enter a model ID for the selected profile first.");
            yield break;
        }
        if (string.IsNullOrWhiteSpace(request.WorkspacePath) || !Directory.Exists(request.WorkspacePath))
        {
            yield return new AgentStreamEvent.RunFailed("Open a workspace folder before starting Pi Agent.");
            yield break;
        }

        string cwd = PathNames.Canonical(request.WorkspacePath);
        bool trusted = WorkspaceTrust.Trusts(cwd);

        SessionHolder? holder = null;
        string? initError = null;
        try
        {
            holder = await GetOrCreateSessionAsync(request, cleanConfig, profile, cwd, trusted, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            PassengerLog.Report(error);
            initError = error.Message;
        }

        if (holder is null)
        {
            yield return new AgentStreamEvent.RunFailed($"Failed to initialize agent session: {initError}");
            yield break;
        }

        Channel<AgentStreamEvent> channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        ChannelWriter<AgentStreamEvent> writer = channel.Writer;
        AgentSession session = holder.Session;

        using IDisposable subscription = session.Agent.Subscribe((ev, _) =>
        {
            switch (ev)
            {
                case AgentEvent.MessageUpdate { AssistantMessageEvent: AssistantMessageEvent.TextDelta delta }:
                    writer.TryWrite(new AgentStreamEvent.TextDelta(delta.Delta));
                    break;
                case AgentEvent.MessageUpdate { AssistantMessageEvent: AssistantMessageEvent.ThinkingDelta thinking }:
                    writer.TryWrite(new AgentStreamEvent.ThinkingDelta(thinking.Delta));
                    break;
                case AgentEvent.ToolExecutionStart start:
                    writer.TryWrite(new AgentStreamEvent.ToolStarted(
                        start.ToolCallId,
                        start.ToolName,
                        WorkspaceScopedTools.LabelOf(start.ToolName),
                        FormatArguments(start.Args)));
                    break;
                case AgentEvent.ToolExecutionEnd end:
                    writer.TryWrite(new AgentStreamEvent.ToolFinished(
                        end.ToolCallId,
                        end.ToolName,
                        ExtractResult(end.Result),
                        end.IsError,
                        end.DurationMs));
                    break;
            }
            return Task.CompletedTask;
        });

        using CancellationTokenRegistration registration = cancel.Register(() => UnawaitedWork.Dismiss(session.AbortAsync()));

        // Background agent turn streams events over an unbounded channel while the UI awaits ReadAllAsync.
        Task run = BackgroundWork.Unheld(async () =>
        {
            try
            {
                await writer.WriteAsync(new AgentStreamEvent.RunStarted(request.SessionId, profile.Model)).ConfigureAwait(false);
                await session.PromptAsync(
                    request.Prompt,
                    new PromptOptions { Source = "interactive", Images = LoadImages(request.Images) }).ConfigureAwait(false);

                AssistantMessage? last = session.Messages.OfType<AssistantMessage>().LastOrDefault();
                SessionStats stats = session.GetSessionStats();
                await writer.WriteAsync(new AgentStreamEvent.RunCompleted(
                    MapStop(last?.StopReason),
                    last?.ErrorMessage,
                    stats.Tokens.Input,
                    stats.Tokens.Output,
                    profile.Model)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                writer.TryWrite(new AgentStreamEvent.RunCompleted(AgentStopReason.Aborted, null, 0, 0, profile.Model));
            }
            catch (Exception error)
            {
                PassengerLog.Report(error);
                writer.TryWrite(new AgentStreamEvent.RunFailed(error.Message));
            }
            finally
            {
                holder.LastHistory = request.History;
                writer.TryComplete();
            }
        });

        await foreach (AgentStreamEvent ev in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return ev;
        }
        await run.ConfigureAwait(false);
    }

    public void Abort(string sessionId)
    {
        SessionHolder? holder;
        lock (sessions)
        {
            sessions.TryGetValue(sessionId, out holder);
        }
        if (holder is not null)
        {
            UnawaitedWork.Dismiss(holder.Session.AbortAsync());
        }
    }

    public void InvalidateSessions()
    {
        lock (sessions)
        {
            sessions.Clear();
        }
    }

    private async Task<SessionHolder> GetOrCreateSessionAsync(
        AgentRunRequest request,
        PiAgentConfig config,
        AgentModelProfile profile,
        string cwd,
        bool trusted,
        CancellationToken cancel)
    {
        IReadOnlyList<AgentHistoryTurn> history = request.History;
        lock (sessions)
        {
            if (sessions.TryGetValue(request.SessionId, out SessionHolder? cached)
                && cached.Matches(history, config, profile, cwd, trusted))
            {
                return cached;
            }
        }

        await sessionGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            lock (sessions)
            {
                if (sessions.TryGetValue(request.SessionId, out SessionHolder? cached)
                    && cached.Matches(history, config, profile, cwd, trusted))
                {
                    return cached;
                }
            }

            AgentSession session = await CreateSessionAsync(config, profile, cwd, trusted, cancel).ConfigureAwait(false);
            InstallWorkspaceGuard(session, cwd);
            SeedHistory(session, history);

            SessionHolder holder = new()
            {
                Session = session,
                LastHistory = history,
                Cwd = cwd,
                Trusted = trusted,
                ProfileId = profile.Id,
                Model = profile.Model,
                ApiKey = profile.ApiKey,
                BaseUrl = profile.BaseUrl,
                Protocol = profile.Protocol,
                Temperature = config.Temperature,
                MaxTokens = config.MaxTokens,
            };
            lock (sessions)
            {
                sessions[request.SessionId] = holder;
            }
            return holder;
        }
        finally
        {
            sessionGate.Release();
        }
    }

    private static async Task<AgentSession> CreateSessionAsync(
        PiAgentConfig config,
        AgentModelProfile profile,
        string cwd,
        bool trusted,
        CancellationToken cancel)
    {
        string api = AgentProtocolCatalog.ApiId(profile.Protocol);
        HttpClient http = OutboundHttp.Client(StreamTimeout, MaxResponseBytes);

        ModelRuntime runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            ModelsPathDisabled = true,
            AllowModelNetwork = false,
            RefreshOnCreate = false,
            Signal = cancel,
        }).ConfigureAwait(false);

        runtime.RegisterProvider(ProviderId, new ProviderConfigInput
        {
            Name = AgentProviderPreset.For(profile.Provider).Name,
            BaseUrl = profile.BaseUrl,
            ApiKey = profile.ApiKey,
            Api = api,
            Models =
            [
                new ProviderChatModelConfig
                {
                    Id = profile.Model,
                    Name = profile.Model,
                    Api = api,
                    BaseUrl = profile.BaseUrl,
                    Input = [ModelInput.Text, ModelInput.Image],
                    Cost = new ModelCostRates(0, 0),
                    Reasoning = false,
                    ContextWindow = profile.ContextWindow ?? 128_000,
                    MaxTokens = config.MaxTokens,
                },
            ],
            StreamSimple = (spec, context, _) =>
            {
                SimpleStreamOptions options = new(
                    ApiKey: profile.ApiKey,
                    BaseUrl: string.IsNullOrWhiteSpace(profile.BaseUrl) ? null : profile.BaseUrl,
                    MaxTokens: config.MaxTokens,
                    Temperature: config.Temperature);
                ModelSpec streamingModel = spec with { Api = api, BaseUrl = profile.BaseUrl };
                return profile.Protocol switch
                {
                    AgentProtocol.AnthropicMessages => AnthropicMessages.StreamSimple(streamingModel, context, options, http, CancellationToken.None),
                    AgentProtocol.OpenAiResponses => OpenAiResponses.StreamSimple(streamingModel, context, options, http, CancellationToken.None),
                    _ => OpenAiCompletions.StreamSimple(streamingModel, context, options, http, CancellationToken.None),
                };
            },
        });

        string agentDir = Config.GetAgentDir();
        SettingsManager settingsManager = SettingsManager.Create(cwd, agentDir, new SettingsManagerCreateOptions { ProjectTrusted = trusted });
        DefaultResourceLoader resourceLoader = new(cwd, agentDir, settingsManager);
        await resourceLoader.ReloadAsync(null, cancel).ConfigureAwait(false);

        IReadOnlyList<ToolDefinition> customTools = WorkspaceScopedTools.CreateCustomTools(cwd, trusted);
        IReadOnlyList<string> tools = [.. WorkspaceScopedTools.BuiltinNames(trusted), .. customTools.Select(t => t.Name)];

        CreateAgentSessionResult created = await Sdk.CreateAgentSessionAsync(
            new CreateAgentSessionOptions
            {
                Cwd = cwd,
                ModelRuntime = runtime,
                ResourceLoader = resourceLoader,
                SettingsManager = settingsManager,
                Model = new ModelSpec
                {
                    Id = profile.Model,
                    Name = profile.Model,
                    Api = api,
                    Provider = ProviderId,
                    BaseUrl = profile.BaseUrl,
                },
                SessionManager = SessionManager.InMemory(cwd),
                Tools = tools,
                CustomTools = customTools,
            },
            processCwd: cwd,
            cancellationToken: cancel).ConfigureAwait(false);

        return created.Session;
    }

    private static void InstallWorkspaceGuard(AgentSession session, string workspaceRoot)
    {
        Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? previous = session.Agent.BeforeToolCall;
        session.Agent.BeforeToolCall = async (context, token) =>
        {
            if (previous is not null)
            {
                BeforeToolCallResult? prior = await previous(context, token).ConfigureAwait(false);
                if (prior?.Block == true)
                {
                    return prior;
                }
            }
            if (WorkspaceScopedTools.CheckCall(context.ToolCall.Name, context.Args, workspaceRoot) is { } reason)
            {
                return new BeforeToolCallResult(Block: true, Reason: reason);
            }
            return null;
        };
    }

    private static void SeedHistory(AgentSession session, IReadOnlyList<AgentHistoryTurn> history)
    {
        List<ChatMessage> messages = [];
        foreach (AgentHistoryTurn turn in history)
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                continue;
            }
            messages.Add(turn.Role == "assistant" ? Messages.AssistantText(turn.Text) : Messages.UserText(turn.Text));
        }
        if (messages.Count == 0)
        {
            return;
        }
        SystemMessage? system = session.Agent.Messages.OfType<SystemMessage>().FirstOrDefault();
        messages.Insert(0, system ?? new SystemMessage(MessageContent.From(string.Empty)));
        session.Agent.Messages = messages;
    }

    private static bool ContinuesHistory(IReadOnlyList<AgentHistoryTurn> previous, IReadOnlyList<AgentHistoryTurn> current)
    {
        if (current.Count < previous.Count)
        {
            return false;
        }
        for (int i = 0; i < previous.Count; i++)
        {
            if (previous[i].Role != current[i].Role || previous[i].Text != current[i].Text)
            {
                return false;
            }
        }
        return true;
    }

    private static AgentStopReason MapStop(StopReason? reason) => reason switch
    {
        StopReason.Aborted => AgentStopReason.Aborted,
        StopReason.Error => AgentStopReason.Error,
        StopReason.Length => AgentStopReason.Length,
        _ => AgentStopReason.Completed,
    };

    private static IReadOnlyList<ImageContent>? LoadImages(IReadOnlyList<AgentAttachment>? images)
    {
        if (images is not { Count: > 0 })
        {
            return null;
        }
        List<ImageContent> list = [];
        foreach (AgentAttachment image in images)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(image.Path) && File.Exists(image.Path))
                {
                    list.Add(new ImageContent(
                        Convert.ToBase64String(File.ReadAllBytes(image.Path)),
                        string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Skip unreadable attachment without failing the entire turn.
            }
        }
        return list.Count > 0 ? list : null;
    }

    private static string FormatArguments(object? args)
    {
        if (args is null)
        {
            return "{}";
        }
        try
        {
            return JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (NotSupportedException)
        {
            return args.ToString() ?? "{}";
        }
    }

    private static string? ExtractResult(object? result)
    {
        if (result is AgentToolResult toolResult)
        {
            string text = string.Join("\n", toolResult.Content.OfType<TextContent>().Select(c => c.Text));
            return text.Length <= 4000 ? text : text[..4000] + "\n…";
        }
        return result?.ToString();
    }

    private sealed class SessionHolder
    {
        public required AgentSession Session { get; init; }
        public IReadOnlyList<AgentHistoryTurn> LastHistory { get; set; } = [];
        public required string Cwd { get; init; }
        public required bool Trusted { get; init; }
        public required string ProfileId { get; init; }
        public required string Model { get; init; }
        public required string ApiKey { get; init; }
        public required string BaseUrl { get; init; }
        public required AgentProtocol Protocol { get; init; }
        public required double Temperature { get; init; }
        public required int MaxTokens { get; init; }

        public bool Matches(
            IReadOnlyList<AgentHistoryTurn> history,
            PiAgentConfig config,
            AgentModelProfile profile,
            string cwd,
            bool trusted) =>
            string.Equals(Cwd, cwd, StringComparison.OrdinalIgnoreCase)
            && Trusted == trusted
            && ProfileId == profile.Id
            && Model == profile.Model
            && ApiKey == profile.ApiKey
            && BaseUrl == profile.BaseUrl
            && Protocol == profile.Protocol
            && Temperature.Equals(config.Temperature)
            && MaxTokens == config.MaxTokens
            && ContinuesHistory(LastHistory, history);
    }
}

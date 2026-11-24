using System.Text;
using System.Text.Json;
using Passenger.Core;
using Pi.Ai.Types;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;

namespace Passenger.Agent;

/// <summary>Summary metadata for one persisted Pi session under the shared ~/.pi/agent/sessions directory.</summary>
public sealed record PiAgentSessionSummary(
    string Id,
    string FilePath,
    string Title,
    DateTimeOffset Created,
    DateTimeOffset Modified,
    int MessageCount,
    string? LastModelId);

public enum PiAgentMessageKind
{
    User,
    Assistant,
    Tool,
}

/// <summary>Restored message/tool record projected from a Pi session JSONL branch.</summary>
public sealed record PiAgentMessageRecord(
    PiAgentMessageKind Kind,
    string Title,
    string Body,
    string Thinking = "");

/// <summary>Lists, loads, renames, and deletes workspace sessions in ~/.pi/agent/sessions/&lt;encoded-cwd&gt;/.</summary>
public static class PiAgentSessionStore
{
    public static string SessionDirectoryFor(string workspacePath, string? agentDir = null)
    {
        string canonical = PathNames.Canonical(workspacePath);
        string dir = string.IsNullOrWhiteSpace(agentDir) ? Config.GetAgentDir() : agentDir;
        return SessionEntries.GetDefaultSessionDirPath(canonical, dir);
    }

    public static async Task<IReadOnlyList<PiAgentSessionSummary>> ListAsync(
        string workspacePath,
        string? agentDir = null,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
        {
            return [];
        }
        string sessionDir = SessionDirectoryFor(workspacePath, agentDir);
        if (!Directory.Exists(sessionDir))
        {
            return [];
        }
        IReadOnlyList<SessionInfo> sessions = await SessionFiles.ListSessionsAsync(sessionDir, null, cancel).ConfigureAwait(false);
        return
        [
            .. sessions
                .Where(s => s.MessageCount > 0 || !string.IsNullOrWhiteSpace(s.Name))
                .OrderByDescending(s => s.Modified)
                .Select(s => new PiAgentSessionSummary(
                    s.Id,
                    s.Path,
                    TitleOf(s),
                    s.Created,
                    s.Modified,
                    s.MessageCount,
                    null)),
        ];
    }

    public static IReadOnlyList<PiAgentMessageRecord> LoadTranscript(string sessionFile, string? workspacePath = null)
    {
        if (string.IsNullOrWhiteSpace(sessionFile) || !File.Exists(sessionFile))
        {
            return [];
        }
        IReadOnlyList<FileEntry> fileEntries = SessionFiles.LoadEntriesFromFile(sessionFile);
        List<SessionEntry> entries = [.. fileEntries.OfType<SessionEntry>()];
        if (entries.Count == 0)
        {
            return [];
        }
        SessionProjection projection = SessionEntries.BuildSessionProjection(entries);
        List<PiAgentMessageRecord> records = [];
        Dictionary<string, int> toolIndices = new(StringComparer.Ordinal);

        foreach (ChatMessage message in projection.Messages)
        {
            switch (message)
            {
                case UserMessage user:
                    string userText = ExtractUserText(user.Content);
                    if (!string.IsNullOrWhiteSpace(userText))
                    {
                        records.Add(new PiAgentMessageRecord(PiAgentMessageKind.User, "You", userText));
                    }
                    break;

                case AssistantMessage assistant:
                    StringBuilder textBuf = new();
                    StringBuilder thinkBuf = new();
                    List<ToolCallContent> calls = [];
                    foreach (ContentBlock block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent text:
                                textBuf.Append(text.Text);
                                break;
                            case ThinkingContent thinking:
                                thinkBuf.Append(thinking.Thinking);
                                break;
                            case ToolCallContent call:
                                calls.Add(call);
                                break;
                        }
                    }
                    string modelTitle = string.IsNullOrWhiteSpace(assistant.Model) ? "Assistant" : assistant.Model;
                    if (textBuf.Length > 0 || thinkBuf.Length > 0 || !string.IsNullOrWhiteSpace(assistant.ErrorMessage))
                    {
                        string body = textBuf.ToString();
                        if (!string.IsNullOrWhiteSpace(assistant.ErrorMessage))
                        {
                            body = body.Length > 0 ? body + "\n\n" + assistant.ErrorMessage : assistant.ErrorMessage;
                        }
                        records.Add(new PiAgentMessageRecord(
                            PiAgentMessageKind.Assistant,
                            modelTitle,
                            body,
                            thinkBuf.ToString()));
                    }
                    foreach (ToolCallContent call in calls)
                    {
                        string argsText = call.Arguments is not null
                            ? JsonSerializer.Serialize(call.Arguments, new JsonSerializerOptions { WriteIndented = true })
                            : "{}";
                        toolIndices[call.Id] = records.Count;
                        records.Add(new PiAgentMessageRecord(
                            PiAgentMessageKind.Tool,
                            WorkspaceScopedTools.LabelOf(call.Name),
                            argsText));
                    }
                    break;

                case ToolResultMessage result:
                    string output = string.Join("\n", result.Content.OfType<TextContent>().Select(c => c.Text));
                    if (toolIndices.TryGetValue(result.ToolCallId, out int idx))
                    {
                        PiAgentMessageRecord existing = records[idx];
                        string duration = result.DurationMs is { } ms ? $" ({ms} ms)" : string.Empty;
                        string status = result.IsError ? " — Failed" : string.Empty;
                        records[idx] = existing with
                        {
                            Title = existing.Title + duration + status,
                            Body = string.IsNullOrWhiteSpace(output) ? existing.Body : existing.Body + "\n\n" + Cap(output),
                        };
                    }
                    else
                    {
                        records.Add(new PiAgentMessageRecord(
                            PiAgentMessageKind.Tool,
                            WorkspaceScopedTools.LabelOf(result.ToolName) + (result.IsError ? " — Failed" : string.Empty),
                            Cap(output)));
                    }
                    break;
            }
        }

        return records;
    }

    public static void RenameSession(string sessionFile, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(sessionFile) || !File.Exists(sessionFile))
        {
            return;
        }
        SessionManager manager = SessionManager.Open(sessionFile);
        manager.AppendSessionInfo(newTitle.Trim());
    }

    public static bool DeleteSession(string sessionFile)
    {
        if (string.IsNullOrWhiteSpace(sessionFile) || !File.Exists(sessionFile))
        {
            return false;
        }
        File.Delete(sessionFile);
        return true;
    }

    private static string TitleOf(SessionInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            return info.Name.Trim();
        }
        string first = (info.FirstMessage ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (first.StartsWith("<attached_context>", StringComparison.Ordinal))
        {
            int end = first.IndexOf("</attached_context>", StringComparison.Ordinal);
            if (end >= 0)
            {
                first = first[(end + "</attached_context>".Length)..].Trim();
            }
        }
        if (first.Length == 0)
        {
            return "Untitled session";
        }
        return first.Length <= 48 ? first : first[..48] + "…";
    }

    private static string ExtractUserText(MessageContent content) =>
        content.TextValue ?? string.Join("\n", content.ToBlocks().OfType<TextContent>().Select(b => b.Text));

    private static string Cap(string text) =>
        text.Length <= 4000 ? text : text[..4000] + "\n…";
}

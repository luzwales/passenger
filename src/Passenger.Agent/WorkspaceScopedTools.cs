using System.Text;
using System.Text.Json;
using Passenger.Core;
using Pi.Agent.Types;
using Pi.Ai.Types;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Passenger.Agent;

/// <summary>Workspace-scoped tools and boundary guards restricting all agent operations to the open folder.</summary>
public static class WorkspaceScopedTools
{
    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "edit",
        "write",
        "write_file",
        "run_command",
        "bash",
        "powershell",
    };

    private static readonly string[] PathKeys = ["path", "file_path", "filePath", "directory", "dir", "root", "target"];

    public static IReadOnlyList<string> BuiltinNames(bool trusted) =>
        trusted ? ["read", "grep", "find", "ls", "edit"] : ["read", "grep", "find", "ls"];

    public static string LabelOf(string name) => name switch
    {
        "read" => "Read File",
        "grep" => "Search Content",
        "find" => "Find Files",
        "ls" => "List Directory",
        "edit" => "Edit File",
        "write" or "write_file" => "Write File",
        "run_command" or "bash" or "powershell" => "Run Command",
        _ => name,
    };

    public static bool IsMutating(string name) => MutatingTools.Contains(name);

    public static IReadOnlyList<ToolDefinition> CreateCustomTools(string workspaceRoot, bool trusted)
    {
        if (!trusted)
        {
            return [];
        }
        return [WriteFileTool(workspaceRoot), RunCommandTool(workspaceRoot)];
    }

    /// <summary>Rejects any tool call that attempts to escape the open folder or mutate an untrusted folder.</summary>
    public static string? CheckCall(string toolName, object? args, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || !Directory.Exists(workspaceRoot))
        {
            return "No open workspace folder is available for tool execution.";
        }
        if (IsMutating(toolName) && !WorkspaceTrust.Trusts(workspaceRoot))
        {
            return WorkspaceTrust.Untrusted;
        }
        if (toolName.StartsWith("mcp__", StringComparison.Ordinal))
        {
            return "External MCP tools are disabled in the embedded workspace agent.";
        }
        JsonElement json = ToElement(args);
        if (json.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (string key in PathKeys)
        {
            if (JsonMembers.Text(json, key) is { Length: > 0 } raw && !IsInsideWorkspace(raw, workspaceRoot, out string resolved))
            {
                return $"Path '{resolved}' is outside the open workspace '{workspaceRoot}'.";
            }
        }
        return null;
    }

    public static string ResolveInside(string path, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Path cannot be empty.");
        }
        if (!IsInsideWorkspace(path, workspaceRoot, out string resolved))
        {
            throw new InvalidOperationException($"Path '{resolved}' is outside the open workspace '{workspaceRoot}'.");
        }
        return resolved;
    }

    private static bool IsInsideWorkspace(string path, string workspaceRoot, out string resolved)
    {
        string root = CanonicalOrFull(workspaceRoot);
        string candidate = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
        resolved = CanonicalOrFull(candidate);
        return PathNames.Within(resolved, root);
    }

    private static string CanonicalOrFull(string path)
    {
        try
        {
            return PathNames.Canonical(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static ToolDefinition WriteFileTool(string workspaceRoot) => new()
    {
        Name = "write_file",
        Label = "Write File",
        Description = "Create or overwrite a UTF-8 text file inside the open workspace folder.",
        Parameters = Schema(("path", "string", "Relative or absolute path inside the open workspace", true),
            ("content", "string", "Complete UTF-8 text to write", true)),
        Execute = (_, args, cancel, _, _) =>
        {
            cancel.ThrowIfCancellationRequested();
            if (!WorkspaceTrust.Trusts(workspaceRoot))
            {
                return Task.FromResult(AgentToolResult.FromError(WorkspaceTrust.Untrusted));
            }
            JsonElement json = ToElement(args);
            string path = JsonMembers.Text(json, "path") ?? throw new InvalidOperationException("Missing required parameter 'path'.");
            string content = JsonMembers.Text(json, "content") ?? throw new InvalidOperationException("Missing required parameter 'content'.");
            string target = ResolveInside(path, workspaceRoot);
            string? folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            AtomicFile.Write(target, Encoding.UTF8.GetBytes(content));
            return Task.FromResult(new AgentToolResult([new TextContent($"Wrote {content.Length} characters to {PathNames.Route(target, workspaceRoot)}.")]));
        },
    };

    private static ToolDefinition RunCommandTool(string workspaceRoot) => new()
    {
        Name = "run_command",
        Label = "Run Command",
        Description = "Run a shell command inside the open trusted workspace folder.",
        Parameters = Schema(("command", "string", "Command line to run in the workspace", true),
            ("timeout_seconds", "integer", "Timeout in seconds (5-300, default 60)", false)),
        Execute = async (_, args, cancel, _, _) =>
        {
            if (!WorkspaceTrust.Trusts(workspaceRoot))
            {
                return AgentToolResult.FromError(WorkspaceTrust.Untrusted);
            }
            JsonElement json = ToElement(args);
            string command = JsonMembers.Text(json, "command") ?? throw new InvalidOperationException("Missing required parameter 'command'.");
            int timeoutSeconds = Math.Clamp(JsonMembers.Number(json, "timeout_seconds") ?? 60, 5, 300);
            (string program, string[] shellArgs) = OperatingSystem.IsWindows()
                ? (Programs.Command("cmd.exe") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/s", "/c", command })
                : ("/bin/bash", new[] { "-lc", command });
            ChildResult result = await ChildProcesses.RunAsync(
                ChildProcesses.Command(program, shellArgs, workspaceRoot),
                limit: TimeSpan.FromSeconds(timeoutSeconds),
                cancel: cancel).ConfigureAwait(false);
            StringBuilder text = new();
            text.AppendLine($"Exit code: {result.ExitCode}");
            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                text.AppendLine("Stdout:");
                text.AppendLine(Cap(result.Output));
            }
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                text.AppendLine("Stderr:");
                text.AppendLine(Cap(result.Error));
            }
            return new AgentToolResult([new TextContent(text.ToString().TrimEnd())]);
        },
    };

    private static ToolSchema Schema(params (string Name, string Type, string Description, bool Required)[] parameters)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        List<string> required = [];
        foreach ((string name, string type, string description, bool isRequired) in parameters)
        {
            properties[name] = new Dictionary<string, object?> { ["type"] = type, ["description"] = description };
            if (isRequired)
            {
                required.Add(name);
            }
        }
        Dictionary<string, object?> schema = new(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
        {
            schema["required"] = required.ToArray();
        }
        return new ToolSchema(schema);
    }

    private static JsonElement ToElement(object? args) => args switch
    {
        null => default,
        JsonElement element => element,
        _ => JsonSerializer.SerializeToElement(args),
    };

    private static string Cap(string text) =>
        text.Length <= 20_000 ? text : text[..20_000] + $"\n... (truncated, {text.Length} chars total)";
}

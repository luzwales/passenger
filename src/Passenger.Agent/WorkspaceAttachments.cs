using System.Text;
using Passenger.Core;

namespace Passenger.Agent;

/// <summary>Reads attached files, folders, and images into prompt-safe payloads and composes prompts.</summary>
public static class WorkspaceAttachments
{
    public const int MaxFileLines = 1000;
    public const int MaxFileChars = 8000;
    public const int MaxFolderEntries = 200;
    public const int MaxFolderChars = 6000;
    public const int DefaultMaxTotalChars = 24000;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".ico", ".avif",
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "artifacts", "bin", "obj", ".vs", ".idea", ".vscode",
        "__pycache__", ".pytest_cache", ".venv", "venv", "dist", "build", "out",
    };

    public static bool IsImagePath(string path) =>
        !string.IsNullOrWhiteSpace(path) && ImageExtensions.Contains(Path.GetExtension(path));

    public static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        _ => "image/png",
    };

    public static AgentAttachment LoadFile(string path, string? workspaceRoot)
    {
        string canonical = SafeCanonical(path);
        string display = DisplayName(canonical, workspaceRoot);
        if (IsImagePath(canonical))
        {
            return LoadImage(canonical, workspaceRoot);
        }
        if (!File.Exists(canonical))
        {
            return new AgentAttachment(canonical, display, AgentAttachmentKind.File, 0, Error: "File not found.");
        }
        try
        {
            long size = new FileInfo(canonical).Length;
            if (IsBinary(canonical))
            {
                return new AgentAttachment(canonical, display, AgentAttachmentKind.File, size);
            }
            (string text, int lines, bool truncated) = ReadTextBounded(canonical);
            return new AgentAttachment(canonical, display, AgentAttachmentKind.File, size, lines, truncated, text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AgentAttachment(canonical, display, AgentAttachmentKind.File, 0, Error: error.Message);
        }
    }

    public static AgentAttachment LoadFolder(string path, string? workspaceRoot)
    {
        string canonical = SafeCanonical(path);
        string display = DisplayName(canonical, workspaceRoot);
        if (!Directory.Exists(canonical))
        {
            return new AgentAttachment(canonical, display, AgentAttachmentKind.Folder, 0, Error: "Folder not found.");
        }
        try
        {
            (string text, int count, bool truncated) = SummarizeFolder(canonical);
            return new AgentAttachment(canonical, display, AgentAttachmentKind.Folder, count, count, truncated, text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AgentAttachment(canonical, display, AgentAttachmentKind.Folder, 0, Error: error.Message);
        }
    }

    public static AgentAttachment LoadImage(string path, string? workspaceRoot)
    {
        string canonical = SafeCanonical(path);
        string display = DisplayName(canonical, workspaceRoot);
        if (!File.Exists(canonical))
        {
            return new AgentAttachment(canonical, display, AgentAttachmentKind.Image, 0, Error: "Image not found.");
        }
        try
        {
            long size = new FileInfo(canonical).Length;
            return new AgentAttachment(canonical, display, AgentAttachmentKind.Image, size, MimeType: MimeTypeOf(canonical));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AgentAttachment(canonical, display, AgentAttachmentKind.Image, 0, Error: error.Message);
        }
    }

    /// <summary>Combines the user's message with attached file/folder contents for the model.</summary>
    public static string ComposePrompt(string text, IReadOnlyList<AgentAttachment>? attachments, int maxTotalChars = DefaultMaxTotalChars)
    {
        List<AgentAttachment> contextItems = [.. (attachments ?? []).Where(a => a.Kind != AgentAttachmentKind.Image)];
        if (contextItems.Count == 0)
        {
            return text;
        }
        StringBuilder builder = new();
        builder.AppendLine(text);
        builder.AppendLine();
        builder.AppendLine("<system-reminder>");
        builder.AppendLine("The user attached the following workspace files or folders to this message:");
        builder.AppendLine("<attached_context>");
        int remaining = maxTotalChars;
        foreach (AgentAttachment item in contextItems)
        {
            if (remaining <= 0)
            {
                builder.AppendLine("[Remaining attachments omitted due to size limit]");
                break;
            }
            builder.AppendLine($"## {item.DisplayPath} ({item.Kind})");
            if (!string.IsNullOrWhiteSpace(item.Error))
            {
                builder.AppendLine($"[Read failed: {Sanitize(item.Error)}]");
                continue;
            }
            if (!item.HasText)
            {
                builder.AppendLine($"[Binary file, {item.Size} bytes]");
                continue;
            }
            string body = TrimToBudget(item.Text!, remaining);
            remaining -= body.Length;
            builder.AppendLine(Sanitize(body));
            if (item.Truncated)
            {
                builder.AppendLine(item.LineCount > 0 ? $"[Truncated after {item.LineCount} items/lines]" : "[Truncated]");
            }
        }
        builder.AppendLine("</attached_context>");
        builder.AppendLine("</system-reminder>");
        return builder.ToString();
    }

    private static string SafeCanonical(string path)
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

    private static string DisplayName(string path, string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot) && PathNames.Within(path, workspaceRoot))
        {
            string rel = PathNames.Route(path, workspaceRoot);
            return rel == "." ? Path.GetFileName(path) : rel;
        }
        return path;
    }

    private static bool IsBinary(string path)
    {
        Span<byte> buffer = stackalloc byte[4096];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int read = stream.Read(buffer);
        return buffer[..read].IndexOf((byte)0) >= 0;
    }

    private static (string Text, int LineCount, bool Truncated) ReadTextBounded(string path)
    {
        StringBuilder builder = new();
        int lines = 0;
        bool truncated = false;
        using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            if (lines >= MaxFileLines || builder.Length + line.Length >= MaxFileChars)
            {
                truncated = true;
                break;
            }
            builder.AppendLine(line);
            lines++;
        }
        return (builder.ToString(), lines, truncated);
    }

    private static (string Text, int EntryCount, bool Truncated) SummarizeFolder(string root)
    {
        StringBuilder builder = new();
        int count = 0;
        bool truncated = false;
        foreach ((string folder, IReadOnlyList<string> files) in PathNames.FilesUnder(root, sub => !IgnoredDirectories.Contains(Path.GetFileName(sub))))
        {
            foreach (string file in files)
            {
                string rel = PathNames.Route(file, root);
                if (count >= MaxFolderEntries || builder.Length + rel.Length >= MaxFolderChars)
                {
                    truncated = true;
                    return (builder.ToString(), count, truncated);
                }
                builder.AppendLine(rel);
                count++;
            }
        }
        return (builder.ToString(), count, truncated);
    }

    private static string TrimToBudget(string text, int budget)
    {
        if (text.Length <= budget)
        {
            return text;
        }
        int cut = budget;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }
        return text[..cut] + "\n[Truncated]";
    }

    private static string Sanitize(string text) => text
        .Replace("<system-reminder>", "<system-reminder-data>", StringComparison.OrdinalIgnoreCase)
        .Replace("</system-reminder>", "<\\/system-reminder>", StringComparison.OrdinalIgnoreCase);
}

using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Passenger.Linq.Runtime;

/// <summary>Streams .Dump() HTML fragments and SQL logs to Passenger's LinqResult window over a Unix domain socket,
/// or falls back to stdout markers when run outside Passenger.</summary>
internal static class LinqDumpSink
{
    public const string SocketVariable = "PASSENGER_LINQ_SOCKET";
    private static readonly Lock Gate = new();
    private static Socket? client;
    private static bool initialized;

    public static void Emit(string kind, string htmlOrText, string? title = null)
    {
        string payload = JsonSerializer.Serialize(new DumpPacket(kind, htmlOrText, title));
        lock (Gate)
        {
            EnsureConnected();
            if (client is not null)
            {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(payload + "\n");
                    int sent = 0;
                    while (sent < bytes.Length)
                    {
                        sent += client.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                    }
                    return;
                }
                catch (SocketException)
                {
                    client.Dispose();
                    client = null;
                }
            }
        }
        Console.WriteLine($"[Dump{(title is null ? "" : $" {title}")}] {StripTags(htmlOrText)}");
    }

    private static void EnsureConnected()
    {
        if (initialized)
        {
            return;
        }
        initialized = true;
        string? socketPath = Environment.GetEnvironmentVariable(SocketVariable);
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return;
        }
        try
        {
            Socket s = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(socketPath));
            client = s;
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException)
        {
            client = null;
        }
    }

    private static string StripTags(string html)
    {
        StringBuilder sb = new(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag)
            {
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }

    private sealed record DumpPacket(string Kind, string Content, string? Title);
}

/// <summary>LINQPad-compatible extension methods available inside every .linq script.</summary>
public static class LinqExtensions
{
    /// <summary>Renders <paramref name="value"/> as a collapsible HTML table in Passenger's LinqResult window and returns it for fluent chaining.</summary>
    public static T Dump<T>(this T value, string? description = null, int maxDepth = DumpHtmlRenderer.DefaultMaxDepth, int maxItems = DumpHtmlRenderer.DefaultMaxItems)
    {
        string html = DumpHtmlRenderer.Render(value, description, maxDepth, maxItems);
        LinqDumpSink.Emit("html", html, description);
        return value;
    }

    /// <summary>Logs a SQL query or diagnostic message to the LinqResult SQL tab.</summary>
    public static void LogSql(string sql, string? source = null)
    {
        LinqDumpSink.Emit("sql", sql, source);
    }
}

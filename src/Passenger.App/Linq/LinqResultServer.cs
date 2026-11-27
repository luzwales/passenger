using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Passenger.Core;
using Passenger.Linq;

namespace Passenger.App.Linq;

/// <summary>Streams <c>.Dump()</c> HTML fragments and SQL logs from a running <c>.linq</c> process over a Unix domain
/// socket in <see cref="SocketFolders.Own"/>, raising <see cref="PacketReceived"/> on any thread.</summary>
public sealed class LinqResultServer : IDisposable
{
    private readonly string socketPath;
    private readonly Socket listener;
    private readonly CancellationTokenSource cts = new();
    private bool disposed;

    public LinqResultServer(string stateRoot)
    {
        string folder = SocketFolders.Own(stateRoot);
        socketPath = Path.Combine(folder, $"{Environment.ProcessId}-linq-{Guid.NewGuid():N}"[..24] + ".sock");
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(8);
        BackgroundWork.Track(AcceptLoopAsync(cts.Token));
    }

    public string SocketPath => socketPath;

    public event EventHandler<LinqDumpPacket>? PacketReceived;

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket client = await listener.AcceptAsync(token);
                BackgroundWork.Track(ReadClientAsync(client, token));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task ReadClientAsync(Socket client, CancellationToken token)
    {
        using (client)
        await using (NetworkStream stream = new(client, ownsSocket: false))
        using (StreamReader reader = new(stream, Encoding.UTF8))
        {
            try
            {
                while (!token.IsCancellationRequested && await reader.ReadLineAsync(token) is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }
                    try
                    {
                        LinqDumpPacket? packet = JsonSerializer.Deserialize<LinqDumpPacket>(line);
                        if (packet is not null)
                        {
                            PacketReceived?.Invoke(this, packet);
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        cts.Cancel();
        listener.Dispose();
        cts.Dispose();
        try
        {
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record LinqDumpPacket(string Kind, string Content, string? Title);

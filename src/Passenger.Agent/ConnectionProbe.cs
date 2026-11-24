using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.Agent;

/// <summary>Sends a minimal ping request to validate endpoint URL, API key, model name, and protocol.</summary>
internal static class ConnectionProbe
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private const long MaxResponseBytes = 1024 * 1024;

    public static async Task<ConnectionTestResult> TestAsync(AgentModelProfile profile, CancellationToken cancel)
    {
        AgentModelProfile clean = profile.Clamped();
        if (clean.ApiKey.Length == 0)
        {
            return ConnectionTestResult.Failure("Missing API Key", "Enter an API key for the selected profile.", 0);
        }
        if (clean.Model.Length == 0)
        {
            return ConnectionTestResult.Failure("Missing Model", "Enter a model ID supported by this endpoint.", 0);
        }
        string baseUrl = clean.BaseUrl.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ConnectionTestResult.Failure("Invalid Base URL", "Base URL must start with http:// or https://.", 0);
        }

        (string path, string payload, bool anthropic) = BuildRequest(clean.Protocol, baseUrl, clean.Model);
        using HttpClient client = OutboundHttp.Client(RequestTimeout, MaxResponseBytes);
        using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Post, new Uri(baseUrl + path));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        if (anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", clean.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + clean.ApiKey);
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancel).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response.IsSuccessStatusCode)
            {
                return ConnectionTestResult.Success(clean.Model, elapsedMs);
            }
            return ConnectionTestResult.Failure($"HTTP {(int)response.StatusCode}", Excerpt(body), elapsedMs);
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return ConnectionTestResult.Failure("Request timed out", "No response within 20 s.", elapsedMs);
        }
        catch (HttpRequestException error)
        {
            long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return ConnectionTestResult.Failure("Connection failed", Excerpt(error.Message), elapsedMs);
        }
    }

    private static (string Path, string Payload, bool Anthropic) BuildRequest(AgentProtocol protocol, string baseUrl, string model) => protocol switch
    {
        AgentProtocol.AnthropicMessages => (
            baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/messages" : "/v1/messages",
            JsonSerializer.Serialize(new { model, max_tokens = 8, messages = new[] { new { role = "user", content = "ping" } } }),
            true),
        AgentProtocol.OpenAiResponses => (
            "/responses",
            JsonSerializer.Serialize(new { model, input = "ping", max_output_tokens = 16 }),
            false),
        _ => (
            "/chat/completions",
            JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = "ping" } }, max_tokens = 8, stream = false }),
            false),
    };

    private static string Excerpt(string text)
    {
        string flat = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }
}

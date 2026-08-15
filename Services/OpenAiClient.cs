using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SoundFluent.Services;

public sealed record ApiTurn(string Role, string Text);

public sealed class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

/// <summary>
/// Thin wrapper over the OpenAI Responses API. Deliberately uses HttpClient and
/// System.Text.Json directly instead of the SDK: no package to keep in sync, and
/// both handle UTF-8 correctly by default, which is the whole ballgame for Polish.
/// </summary>
public sealed class OpenAiClient : IDisposable
{
    private const string Endpoint = "https://api.openai.com/v1/responses";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary>
    /// First turn: detects the input language and returns only natural Polish text.
    /// </summary>
    public async Task<string> PolishAsync(
        string apiKey, string model, string sourceText, CancellationToken ct)
    {
        var turns = new List<ApiTurn>
        {
            new("system", Prompts.Polish),
            new("user", sourceText)
        };

        string raw = await SendAsync(apiKey, model, turns, Prompts.ResponseFormat, ct)
            .ConfigureAwait(false);

        return ParsePolishedText(raw);
    }

    /// <summary>Later turns: free-form reply, full history included for context.</summary>
    public async Task<string> FollowUpAsync(
        string apiKey, string model, IReadOnlyList<ApiTurn> history, CancellationToken ct)
    {
        var turns = new List<ApiTurn> { new("system", Prompts.FollowUp) };
        turns.AddRange(history);

        return await SendAsync(apiKey, model, turns, null, ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(
        string apiKey, string model, IReadOnlyList<ApiTurn> turns, string? responseFormatJson,
        CancellationToken ct)
    {
        var input = new JsonArray();
        foreach (ApiTurn turn in turns)
        {
            input.Add(new JsonObject
            {
                ["role"] = turn.Role,
                ["content"] = turn.Text
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = input
        };

        if (responseFormatJson is not null)
            body["text"] = JsonNode.Parse(responseFormatJson);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new ApiException(ExtractApiError(payload, (int)response.StatusCode));

        return ExtractOutputText(payload);
    }

    /// <summary>
    /// Pulls the assistant text out of the `output` array. Filters by block type
    /// rather than taking output[0] — reasoning models put a reasoning block first,
    /// and indexing blindly is the classic way to get an empty string back.
    /// </summary>
    private static string ExtractOutputText(string payload)
    {
        JsonNode? root = JsonNode.Parse(payload);
        JsonArray? output = root?["output"]?.AsArray();
        if (output is null) throw new ApiException("The API response had no output.");

        var sb = new StringBuilder();
        foreach (JsonNode? item in output)
        {
            if (item?["type"]?.GetValue<string>() != "message") continue;

            JsonArray? content = item["content"]?.AsArray();
            if (content is null) continue;

            foreach (JsonNode? block in content)
            {
                if (block?["type"]?.GetValue<string>() != "output_text") continue;
                sb.Append(block["text"]?.GetValue<string>());
            }
        }

        string text = sb.ToString().Trim();
        if (text.Length == 0) throw new ApiException("The API returned an empty response.");
        return text;
    }

    private static string ParsePolishedText(string json)
    {
        // Strip stray fences in case the model wraps the JSON despite strict mode.
        string cleaned = json.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            int first = cleaned.IndexOf('\n');
            int last = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (first >= 0 && last > first) cleaned = cleaned[(first + 1)..last].Trim();
        }

        try
        {
            JsonNode? root = JsonNode.Parse(cleaned);
            string corrected = root?["corrected"]?.GetValue<string>() ?? "";
            if (corrected.Length == 0) throw new ApiException("The correction came back empty.");
            return corrected;
        }
        catch (JsonException)
        {
            // Fall back to treating the whole reply as the corrected text.
            return cleaned;
        }
    }

    private static string ExtractApiError(string payload, int statusCode)
    {
        try
        {
            string? message = JsonNode.Parse(payload)?["error"]?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message)) return message!;
        }
        catch
        {
            // Non-JSON error body (proxy, gateway); fall through to the generic message.
        }

        return statusCode switch
        {
            401 => "The API key was rejected. Set a new one from the tray icon.",
            429 => "Rate limited, or the account is out of credit.",
            404 => "That model id doesn't exist. Change it in settings.json.",
            _ => $"The API returned HTTP {statusCode}."
        };
    }

    public void Dispose() => _http.Dispose();
}

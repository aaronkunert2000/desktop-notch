using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notch.Services;

/// <summary>Legt eine neue Seite in einer Notion-Datenbank an (Notion-API) und liefert deren Adresse.</summary>
public static class NotionNoteService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <param name="titleProperty">Name der Titel-Eigenschaft der Datenbank (bei [DB] Notes: "Name").</param>
    public static async Task<string> CreateAsync(string token, string databaseId, string titleProperty, string title)
    {
        var body = new JsonObject
        {
            ["parent"] = new JsonObject { ["database_id"] = databaseId },
            ["properties"] = new JsonObject
            {
                [titleProperty] = new JsonObject
                {
                    ["title"] = new JsonArray(new JsonObject
                    {
                        ["text"] = new JsonObject { ["content"] = title },
                    }),
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.notion.com/v1/pages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Notion-Version", "2022-06-28");

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadMessage(text) ?? response.ReasonPhrase ?? "Fehler";
            throw new InvalidOperationException($"Notion {(int)response.StatusCode}: {message}");
        }

        var url = JsonNode.Parse(text)?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("Notion lieferte keine Seiten-Adresse");
        return url;
    }

    /// <summary>https://www.notion.so/… → notion://www.notion.so/… (öffnet die Desktop-App statt des Browsers).</summary>
    public static string ToAppUrl(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "notion://" + url["https://".Length..] : url;

    private static string? TryReadMessage(string json)
    {
        try { return JsonNode.Parse(json)?["message"]?.GetValue<string>(); }
        catch (JsonException) { return null; }
    }
}

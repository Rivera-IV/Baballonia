using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = ProxyOptions.Parse(args);
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);
builder.Services.AddHttpClient("lmstudio", client =>
{
    client.BaseAddress = new Uri(options.UpstreamUrl);
    client.Timeout = Timeout.InfiniteTimeSpan;
});

var app = builder.Build();
app.MapGet("/health", () => Results.Json(new { status = "ok", upstream = options.UpstreamUrl }));
// Codex 0.149 requests its own optional model-metadata schema here. An empty catalog makes it use
// safe fallback metadata for the explicitly selected local model without contacting cloud catalogs.
app.MapGet("/v1/models", () => Results.Json(new { models = Array.Empty<object>() }));
app.MapFallback(ForwardRequest);
await app.RunAsync();

async Task ForwardRequest(HttpContext context)
{
    if (context.Connection.RemoteIpAddress is not null &&
        !IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var path = context.Request.Path.Value ?? "/";
    using var request = new HttpRequestMessage(
        new HttpMethod(context.Request.Method),
        path + context.Request.QueryString);

    if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        if (HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var original = await reader.ReadToEndAsync(context.RequestAborted);
            var (body, mergedMessages) = MergeDeveloperInstructions(original);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (mergedMessages > 0)
                app.Logger.LogInformation("Merged {Count} additional system/developer message(s).", mergedMessages);
        }
        else
        {
            request.Content = new StreamContent(context.Request.Body);
        }
    }

    CopyRequestHeaders(context.Request, request);
    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("lmstudio");
    using var response = await client.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        context.RequestAborted);

    context.Response.StatusCode = (int)response.StatusCode;
    CopyResponseHeaders(response, context.Response);
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

static (string Body, int MergedMessages) MergeDeveloperInstructions(string json)
{
    var root = JsonNode.Parse(json)?.AsObject()
        ?? throw new InvalidDataException("The Responses request body is not a JSON object.");
    if (root["input"] is not JsonArray input)
        return (json, 0);

    var instructionParts = new List<string>();
    if (root["instructions"] is JsonValue instructionValue &&
        instructionValue.TryGetValue<string>(out var existing) &&
        !string.IsNullOrWhiteSpace(existing))
    {
        instructionParts.Add(existing);
    }

    var retained = new JsonArray();
    var merged = 0;
    foreach (var item in input)
    {
        if (item is JsonObject message &&
            message["role"]?.GetValue<string>() is { } role &&
            (role.Equals("developer", StringComparison.OrdinalIgnoreCase) ||
             role.Equals("system", StringComparison.OrdinalIgnoreCase)))
        {
            var text = ExtractText(message["content"]);
            if (!string.IsNullOrWhiteSpace(text)) instructionParts.Add(text);
            merged++;
            continue;
        }

        retained.Add(item?.DeepClone());
    }

    if (merged == 0) return (json, 0);
    root["input"] = retained;
    root["instructions"] = string.Join("\n\n", instructionParts);
    return (root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), merged);
}

static string ExtractText(JsonNode? content)
{
    if (content is JsonValue value && value.TryGetValue<string>(out var direct)) return direct;
    if (content is not JsonArray parts) return string.Empty;

    return string.Join("\n", parts
        .OfType<JsonObject>()
        .Select(part => part["text"]?.GetValue<string>())
        .Where(text => !string.IsNullOrWhiteSpace(text)));
}

static void CopyRequestHeaders(HttpRequest source, HttpRequestMessage destination)
{
    foreach (var header in source.Headers)
    {
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            continue;

        if (!destination.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            destination.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }
}

static void CopyResponseHeaders(HttpResponseMessage source, HttpResponse destination)
{
    foreach (var header in source.Headers.Concat(source.Content.Headers))
    {
        if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            continue;
        destination.Headers[header.Key] = header.Value.ToArray();
    }
}

internal sealed record ProxyOptions(string ListenUrl, string UpstreamUrl)
{
    public static ProxyOptions Parse(string[] arguments)
    {
        var listen = Value(arguments, "--listen") ?? "http://127.0.0.1:1235";
        var upstream = Value(arguments, "--upstream") ?? "http://127.0.0.1:1234";
        foreach (var url in new[] { listen, upstream })
        {
            var uri = new Uri(url);
            if (!IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address))
                throw new ArgumentException($"Only loopback addresses are allowed: {url}");
        }
        return new ProxyOptions(listen.TrimEnd('/'), upstream.TrimEnd('/') + "/");
    }

    private static string? Value(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }
}

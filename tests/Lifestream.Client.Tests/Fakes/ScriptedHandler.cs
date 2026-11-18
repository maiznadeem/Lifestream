using System.Net;
using System.Text;

namespace Lifestream.Client.Tests.Fakes;

/// <summary>A message handler that answers from a script and records every request (method, uri, headers, body).</summary>
public sealed class ScriptedHandler(Func<RecordedRequest, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        Json(status, $$"""{"title":"{{code}}","status":{{(int)status}},"code":"{{code}}"}""");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            body,
            request.Version,
            request.VersionPolicy);
        int index;
        lock (_requests)
        {
            _requests.Add(recorded);
            index = _requests.Count;
        }

        return respond(recorded, index);
    }
}

public sealed record RecordedRequest(HttpMethod Method, string Uri, IReadOnlyDictionary<string, string> Headers, string? Body, Version Version, HttpVersionPolicy VersionPolicy)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

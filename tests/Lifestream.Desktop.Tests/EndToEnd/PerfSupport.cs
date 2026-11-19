using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Lifestream.Client;
using Lifestream.Client.Api;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Updates;
using Lifestream.Contracts.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Desktop.Tests.EndToEnd;

/// <summary>
/// What the perf pass needs from its environment. <c>tests/perf/run-perf.ps1</c> sets all of it: the local API it started, a fixture database's
/// administrator (the password lives only in this process's environment) and the file the measurements are appended to.
/// </summary>
internal static class PerfEnv
{
    public static string ApiBase => Required("LIFESTREAM_PERF_API");

    public static string User => Required("LIFESTREAM_PERF_USER");

    public static string Password => Required("LIFESTREAM_PERF_PASSWORD");

    /// <summary>"1x", "10x" or "100x": which fixture database the API is running over.</summary>
    public static string Label => Required("LIFESTREAM_PERF_LABEL");

    /// <summary>How many receipts the fixture holds, so a test proves it really ran over that dataset.</summary>
    public static int ExpectedReceipts => int.Parse(Required("LIFESTREAM_PERF_RECEIPTS"), CultureInfo.InvariantCulture);

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set: the Category=Perf tests are driven by tests/perf/run-perf.ps1, which starts the local API and sets it.");
}

/// <summary>
/// Appends one JSON line per measurement to <c>LIFESTREAM_PERF_RESULTS</c>; the script turns the lines into docs/perf/results.md. A measurement that could
/// not be taken is a row of its own (<see cref="Missed"/>, never an infinite value), and a test declares up front the rows it will record
/// (<see cref="Expect"/>) so the script can tell a row that never came from a run that is complete.
/// </summary>
internal static class PerfLog
{
    private static readonly Lock Gate = new();

    /// <summary>Where the lines go; the self-tests point it at a file of their own (per thread), the perf run at LIFESTREAM_PERF_RESULTS.</summary>
    [ThreadStatic]
    internal static string? PathOverride;

    private static string? ResultsPath => PathOverride ?? (Environment.GetEnvironmentVariable("LIFESTREAM_PERF_RESULTS") is { Length: > 0 } path ? path : null);

    /// <summary>Records one measurement. <paramref name="key"/> names the row for the "every expected row is present" check (default: the budget).</summary>
    public static bool Record(string budget, string name, string stat, int n, double value, string unit, string limit, bool pass, string detail = "", string? key = null)
    {
        if (!double.IsFinite(value))
        {
            return Missed(budget, name, stat, n, limit, $"MISSED {key ?? budget}: {name} has no finite value ({value}). {detail}".TrimEnd(), key);
        }

        Append(new Dictionary<string, object?>
        {
            ["budget"] = budget,
            ["key"] = key ?? budget,
            ["name"] = name,
            ["dataset"] = Environment.GetEnvironmentVariable("LIFESTREAM_PERF_LABEL") ?? "",
            ["stat"] = stat,
            ["n"] = n,
            ["value"] = Math.Round(value, 1),
            ["unit"] = unit,
            ["limit"] = limit,
            ["pass"] = pass,
            ["detail"] = detail,
        });
        return pass;
    }

    /// <summary>A measurement that could not be taken (a skeleton that never showed, a screen that did not load): a failed row with no value, never Infinity. <paramref name="detail"/> is the whole sentence, starting "MISSED &lt;key&gt;: ". Always returns false.</summary>
    public static bool Missed(string budget, string name, string stat, int n, string limit, string detail, string? key = null)
    {
        Append(new Dictionary<string, object?>
        {
            ["budget"] = budget,
            ["key"] = key ?? budget,
            ["name"] = name,
            ["dataset"] = Environment.GetEnvironmentVariable("LIFESTREAM_PERF_LABEL") ?? "",
            ["stat"] = stat,
            ["n"] = n,
            ["value"] = 0.0,
            ["unit"] = "",
            ["limit"] = limit,
            ["pass"] = false,
            ["missed"] = true,
            ["detail"] = detail,
        });
        return false;
    }

    /// <summary>Declares the row keys this test is about to record; the script fails the run when any of them has no row.</summary>
    public static void Expect(params IEnumerable<string> keys) =>
        Append(new Dictionary<string, object?>
        {
            ["expect"] = keys.ToArray(),
            ["dataset"] = Environment.GetEnvironmentVariable("LIFESTREAM_PERF_LABEL") ?? "",
        });

    private static void Append(Dictionary<string, object?> row)
    {
        var line = JsonSerializer.Serialize(row);
        if (ResultsPath is { } path)
        {
            lock (Gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }

    /// <summary>The p95 of a sample, nearest rank: the ceil(0.95 n)-th smallest value.</summary>
    public static double P95(IReadOnlyList<double> samples)
    {
        var sorted = samples.Order().ToList();
        return sorted[(int)Math.Ceiling(sorted.Count * 0.95) - 1];
    }

    public static double Median(IReadOnlyList<double> samples)
    {
        var sorted = samples.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }
}

/// <summary>
/// Sits under the client's wake-aware handler, over the real socket: counts every request (and whether it started on the UI thread), adds the
/// injected latency before sending, and stamps the moment each response body is fully in memory (data arrival).
/// </summary>
internal sealed class ProbeHandler(HttpMessageHandler inner, int uiThreadId) : DelegatingHandler(inner)
{
    private readonly ConcurrentQueue<string> _calls = new();
    private int _onUiThread;
    private long _lastArrival;

    public TimeSpan Latency { get; set; }

    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>How many requests were started on the UI thread (must be none: no API call may run there, SHELL-09).</summary>
    public int CallsStartedOnUiThread => Volatile.Read(ref _onUiThread);

    /// <summary><see cref="Stopwatch.GetTimestamp"/> when the last response had fully arrived.</summary>
    public long LastArrival => Volatile.Read(ref _lastArrival);

    public void Reset()
    {
        _calls.Clear();
        Volatile.Write(ref _onUiThread, 0);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _calls.Enqueue($"{request.Method} {request.RequestUri!.AbsolutePath}");
        if (Environment.CurrentManagedThreadId == uiThreadId)
        {
            Interlocked.Increment(ref _onUiThread);
        }

        if (Latency > TimeSpan.Zero)
        {
            await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _lastArrival, Stopwatch.GetTimestamp());
        return response;
    }
}

/// <summary>
/// The real client stack (real <see cref="LifestreamApiClient"/>, wake-aware handler, one HttpClient, real session and navigation) over a real socket to the
/// local API, signed in as the fixture's administrator. Create it on the STA thread that hosts the views.
/// </summary>
internal sealed class RealRig : IDisposable
{
    private RealRig(ServiceProvider provider, ProbeHandler probe)
    {
        Provider = provider;
        Probe = probe;
    }

    public ServiceProvider Provider { get; }

    public ProbeHandler Probe { get; }

    public ShellViewModel Shell => Provider.GetRequiredService<ShellViewModel>();

    /// <summary>The window host the app flow talks to (the fake one: no window is ever created); after "Try the demo" its <c>Main</c> is the shell the flow opened.</summary>
    public FakeWindowHost Windows => (FakeWindowHost)Provider.GetRequiredService<IWindowHost>();

    /// <summary>The client stack over the API at <paramref name="apiBase"/> (default: the perf run's), not signed in: the sign-in screen is the caller's to drive.</summary>
    public static RealRig CreateSignedOut(string? apiBase = null) => Create(apiBase, signIn: false);

    public static RealRig Create(string? apiBase = null, bool signIn = true)
    {
        var baseUri = new Uri(apiBase ?? PerfEnv.ApiBase);
        var probe = new ProbeHandler(
            new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Brotli },
            Environment.CurrentManagedThreadId);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLifestreamClient(baseUri, new Version(2, 0, 0));
        // The one HttpClient of the app, replaced by the same client with the probe underneath (the last registration wins).
        services.AddSingleton(p => new HttpClient(new WakeAwareHandler(p.GetRequiredService<WakePolicy>(), p.GetRequiredService<ServerStatus>(), probe))
        {
            BaseAddress = baseUri,
            DefaultRequestVersion = System.Net.HttpVersion.Version11, // the local API listens on plain http, where HTTP/2 needs prior knowledge; the deployed app uses HTTP/2 over TLS
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan,
        });
        services.AddSingleton<IUpdateSource>(new FakeUpdateSource());
        services.AddSingleton<IWindowHost>(new FakeWindowHost());
        services.AddSingleton(WpfTestUi.Dispatcher());
        var provider = services.BuildServiceProvider();

        if (!signIn)
        {
            return new RealRig(provider, probe);
        }

        var api = provider.GetRequiredService<ILifestreamApiClient>();
        var login = api.LoginAsync(new LoginRequest(PerfEnv.User, PerfEnv.Password)).GetAwaiter().GetResult();
        if (!login.IsOk)
        {
            throw new InvalidOperationException("the perf administrator could not sign in: " + login.Problem?.Code);
        }

        provider.GetRequiredService<ISessionService>().Establish(login.Value);
        probe.Reset();
        return new RealRig(provider, probe);
    }

    public void Dispose() => Provider.Dispose();
}

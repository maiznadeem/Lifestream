using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Auth;
using Lifestream.Client.Navigation;
using Lifestream.Client.Session;
using Lifestream.Client.Shell;
using Lifestream.Client.Updates;
using Lifestream.Contracts.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Client.Tests.Fakes;

public sealed class FakeWindowHost : IWindowHost
{
    public List<string> Events { get; } = [];

    public LoginViewModel? Login { get; private set; }

    public ShellViewModel? Main { get; private set; }

    public void ShowLogin(LoginViewModel viewModel)
    {
        Login = viewModel;
        Events.Add("ShowLogin");
    }

    public void CloseLogin() => Events.Add("CloseLogin");

    public void ShowMain(ShellViewModel viewModel)
    {
        Main = viewModel;
        Events.Add("ShowMain");
    }

    public bool ThrowOnCloseMain { get; set; }

    public void CloseMain()
    {
        Events.Add("CloseMain");
        if (ThrowOnCloseMain)
        {
            throw new InvalidOperationException("window already gone");
        }
    }
}

public sealed class FakeUpdateSource(Version? latest = null) : IUpdateSource
{
    public Version? Latest { get; set; } = latest;

    public bool Throw { get; set; }

    public int Applied { get; private set; }

    public Task<Version?> GetLatestVersionAsync(CancellationToken ct) =>
        Throw ? throw new HttpRequestException("feed down") : Task.FromResult(Latest);

    public Task ApplyAsync(CancellationToken ct)
    {
        Applied++;
        return Task.CompletedTask;
    }
}

/// <summary>The real client services wired to the fake API, a fake window host and a fake update source.</summary>
public sealed class Rig : IDisposable
{
    public Rig(FakeApiClient? api = null, Version? latestVersion = null, Version? currentVersion = null, IUiDispatcher? dispatcher = null)
    {
        Api = api ?? new FakeApiClient();
        Updates = new FakeUpdateSource(latestVersion);
        Windows = new FakeWindowHost();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLifestreamClient(new Uri("https://api.test/"), currentVersion ?? new Version(2, 0, 0));
        services.AddSingleton<ILifestreamApiClient>(Api);
        services.AddSingleton<IUpdateSource>(Updates);
        services.AddSingleton<IWindowHost>(Windows);
        services.AddSingleton(dispatcher ?? new InlineUiDispatcher());
        Provider = services.BuildServiceProvider();
    }

    public FakeApiClient Api { get; }

    public FakeUpdateSource Updates { get; }

    public FakeWindowHost Windows { get; }

    public ServiceProvider Provider { get; }

    public T Get<T>()
        where T : notnull => Provider.GetRequiredService<T>();

    public ISessionService Session => Get<ISessionService>();

    public void SignIn(Lifestream.Domain.Role role = Lifestream.Domain.Role.Staff, bool demo = false) =>
        Session.Establish(new TokenResponse("a", "r", DateTime.UtcNow.AddMinutes(15), FakeApiClient.User(role, demo)));

    public void Dispose() => Provider.Dispose();
}

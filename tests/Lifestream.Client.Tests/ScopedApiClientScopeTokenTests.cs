using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Tests.Fakes;

namespace Lifestream.Client.Tests;

/// <summary>
/// Mutation gap (SHELL-07): every call site passes the runner's token explicitly, so removing the scope-token link from <see cref="ScopedApiClient"/> left
/// every test green. These tests call the API with a token that is NOT the scope's (none at all, and an unrelated one) and cancel only the operation: the
/// request that is waiting at the server must see the cancellation. They fail when the scope's token is not part of the call's linked token.
/// </summary>
[Collection("GlobalErrorHandler")]
public sealed class ScopedApiClientScopeTokenTests
{
    private static (ILifestreamApiClient Api, FakeApiClient Fake, List<CancellationToken> Seen) Rig()
    {
        var fake = new FakeApiClient();
        var seen = new List<CancellationToken>();
        fake.Handlers["GetDonorsAsync"] = async ct =>
        {
            lock (seen)
            {
                seen.Add(ct);
            }

            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        };
        return (ScopedApiClient.Wrap(fake, strict: true), fake, seen);
    }

    private static async Task<CancellationToken> WaitUntilTheCallArrives(List<CancellationToken> seen)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (seen)
            {
                if (seen.Count > 0)
                {
                    return seen[0];
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("the call never reached the server stub");
    }

    [Fact]
    public async Task CancellingTheOperation_CancelsACallThatWasGivenNoToken_AtTheServerStub()
    {
        var (api, _, seen) = Rig();
        using var runner = new OperationRunner(() => null, () => TimeProvider.System);

        var operation = runner.ScopeAsync(_ => api.GetDonorsAsync(new(), CancellationToken.None)); // the caller's token is not the scope's
        var token = await WaitUntilTheCallArrives(seen);
        Assert.False(token.IsCancellationRequested);

        runner.Cancel();

        Assert.True(token.IsCancellationRequested, "Cancel did not reach the call: the scope's token is not linked into the call's token");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task CancellingTheOperation_CancelsACallThatWasGivenAnUnrelatedToken_AtTheServerStub()
    {
        var (api, _, seen) = Rig();
        using var runner = new OperationRunner(() => null, () => TimeProvider.System);
        using var unrelated = new CancellationTokenSource();

        var operation = runner.ScopeAsync(_ => api.GetDonorsAsync(new(), unrelated.Token));
        var token = await WaitUntilTheCallArrives(seen);

        runner.Cancel();

        Assert.True(token.IsCancellationRequested, "Cancel did not reach the call: the scope's token is not linked into the call's token");
        Assert.False(unrelated.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task ACallThatIsNotCancelled_KeepsItsOwnTokenWorking_AtTheServerStub()
    {
        var (api, _, seen) = Rig();
        using var runner = new OperationRunner(() => null, () => TimeProvider.System);
        using var own = new CancellationTokenSource();

        var operation = runner.ScopeAsync(_ => api.GetDonorsAsync(new(), own.Token));
        var token = await WaitUntilTheCallArrives(seen);
        await own.CancelAsync();

        Assert.True(token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }
}

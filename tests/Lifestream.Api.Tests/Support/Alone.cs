namespace Lifestream.Api.Tests.Support;

/// <summary>
/// The classes whose result depended on what else was running in the test process at that moment. xUnit runs a collection that has
/// parallelisation off after the parallel collections, one class at a time, so each of them has the database and the process to itself.
/// No assertion, burst size or timeout is changed by being here.
/// <para>
/// Bursts. <c>LoginLockoutConcurrencyTests</c> (120 sign-ins at once), <c>AdminRuleConcurrencyTests</c> (two administrators demoting each
/// other for ten rounds) and <c>Data.DemoResetTests</c> (ten demo resets at once) fire a burst of concurrent requests and assert on every
/// answer. Each needs SQL Server to answer within seconds: the API's connection pool holds 100 connections and a command waits 30 s. What
/// else runs at that moment is decided by xUnit's order, which comes from the checkout's path: in the order of a GitHub runner's path
/// (/home/runner/work/lifestream/lifestream), on 4 processors shared with SQL Server, the 120-sign-in test got 9 to 20 answers of 500
/// instead of 401 in 4 unseeded runs out of 5, while other paths and both fixed seeds usually passed.
/// </para>
/// <para>
/// Process-wide counts. <c>DemoCapTests</c> counts demo writes per address and day, and the API keeps those counts in one static table for
/// the process (one API per process in production; every test class's API in the same process here). A demo write handled by an API whose
/// clock is on a later day (the daily-reset tests move theirs) clears every earlier day's counts, this class's among them, and its fourth
/// write was then answered 201 instead of 429: seen in 3 of about 25 runs on Linux.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Alone
{
    public const string Name = "Alone: one class at a time, after the parallel classes";
}

namespace Lifestream.Api.Tests.Support;

/// <summary>
/// The classes that fire a burst of concurrent requests and assert on every answer: 120 sign-ins at once, two administrators demoting each
/// other for ten rounds, ten demo resets at once. Each needs SQL Server to answer its burst within seconds (the API's connection pool holds
/// 100 connections and a command waits 30 s), so beside the rest of the suite its result depended on what else was running at that moment.
/// That is decided by xUnit's order, which comes from the checkout's path: in the order of a GitHub runner's path
/// (/home/runner/work/lifestream/lifestream), on 4 processors shared with SQL Server, the 120-sign-in test got 9 to 20 answers of 500 instead
/// of 401 in every unseeded run, while the order of other paths, and both fixed seeds, usually passed.
/// <para>
/// xUnit runs a collection that has parallelisation off after the parallel collections, one class at a time, so each burst has the database
/// to itself. No assertion is changed.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Bursts
{
    public const string Name = "Bursts: one class at a time, after the parallel classes";
}

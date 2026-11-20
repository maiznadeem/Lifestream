using Lifestream.Api.Tests.Data;
using Xunit.Sdk;
using Xunit.v3;

[assembly: TestCollectionOrderer(typeof(Lifestream.Api.Tests.Support.ScaleFirstCollectionOrderer))]

namespace Lifestream.Api.Tests.Support;

/// <summary>
/// Starts the classes on a copy of the 100x database first: the first of them builds the run's template (about half a minute) and the plan matrix is the
/// longest work of the run, so when xUnit happened to start them last they were the tail of the whole run. Every other collection keeps xUnit's default
/// order, which is random from run to run.
/// </summary>
public sealed class ScaleFirstCollectionOrderer : ITestCollectionOrderer
{
    private static readonly string[] ScaleClasses =
    [
        nameof(ApiPlanMatrixTests),
        nameof(ApiPlanMatrixPart2Tests),
        nameof(ApiPlanMatrixPart3Tests),
        nameof(ApiPlanMatrixPart4Tests),
        nameof(ApiWritePlanMatrixTests),
        nameof(ListQueryPlanTests),
        nameof(PlanTests),
    ];

    public IReadOnlyCollection<TTestCollection> OrderTestCollections<TTestCollection>(IReadOnlyCollection<TTestCollection> testCollections)
        where TTestCollection : notnull, ITestCollection
    {
        var ordered = DefaultTestCollectionOrderer.Instance.OrderTestCollections(testCollections);
        return [.. ordered.Where(c => IsScale(c)), .. ordered.Where(c => !IsScale(c))];
    }

    private static bool IsScale(ITestCollection collection) =>
        ScaleClasses.Any(name => collection.TestCollectionDisplayName.EndsWith("." + name, StringComparison.Ordinal)
            || collection.TestCollectionDisplayName.Contains("." + name + " ", StringComparison.Ordinal));
}

using System.Reflection;
using Lifestream.Client.Api;
using Lifestream.Client.Common;

namespace Lifestream.Client.Tests;

/// <summary>
/// SHELL-07 inventory. These tests enumerate every view model, every command and every place that calls the API by reflection (and by reading the IL),
/// so a new surface cannot be missed: an async operation that does not run through the operation runner fails here, not in a verifier's next round.
/// </summary>
public sealed class OperationInventoryTests : IDisposable
{
    private readonly ViewModelCatalog _catalog = new();

    public void Dispose() => _catalog.Dispose();

    private static IEnumerable<Type> ViewModelTypes() =>
        typeof(ViewModelBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsNested: false } && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .Where(t => typeof(ViewModelBase).IsAssignableFrom(t) || t.GetProperties().Any(p => p.PropertyType == typeof(AsyncRelayCommand)))
            .Where(t => !t.IsGenericTypeDefinition);

    [Fact]
    public void EveryViewModelType_IsInTheCatalog_SoNewOnesCannotBeMissed()
    {
        var missing = ViewModelTypes().Where(t => !_catalog.Factories.ContainsKey(t)).Select(t => t.FullName).Order().ToList();

        Assert.True(missing.Count == 0, "View model types missing from ViewModelCatalog (add a factory so the inventory covers them): " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryCommand_OfEveryViewModel_RunsThroughTheOperationRunner_OrCarriesAReviewedExemption()
    {
        var checkedCommands = 0;
        var problems = new List<string>();
        foreach (var (type, create) in _catalog.Factories.Where(f => ViewModelTypes().Contains(f.Key)))
        {
            var instance = create();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(AsyncRelayCommand)))
            {
                var command = (AsyncRelayCommand?)property.GetValue(instance);
                if (command is null)
                {
                    continue;
                }

                checkedCommands++;
                var exemption = property.GetCustomAttribute<NoOperationRunnerAttribute>();
                if (exemption is not null)
                {
                    if (exemption.Reason.Length < 15)
                    {
                        problems.Add($"{type.Name}.{property.Name}: the exemption needs a reason");
                    }

                    continue;
                }

                var owner = (instance as ViewModelBase)?.Operations ?? (instance as Lookups.RecordPickerViewModel)?.Operations ?? (instance as ToastItem)?.Operations;
                if (command.Runner is null || !ReferenceEquals(command.Runner, owner))
                {
                    problems.Add($"{type.Name}.{property.Name}: not bound to the view model's operation runner");
                }
            }
        }

        Assert.True(checkedCommands > 100, $"only {checkedCommands} commands were inspected: the inventory is not walking the view models");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void NoViewModelMethod_CallsTheApiDirectly_UnlessItIsALambdaForTheRunnerOrAReviewedHook()
    {
        var sites = ApiCallSiteScanner.Scan(typeof(ViewModelBase).Assembly)
            .Where(s => RootNamespace(s.Type) is "Lifestream.Client.Features" or "Lifestream.Client.Auth" or "Lifestream.Client.Lookups" or "Lifestream.Client.Shell" or "Lifestream.Client.Common")
            .ToList();
        var violations = sites.Where(s => !IsOperationCode(s.Type, s.Method)).Select(s => $"{Describe(s.Type)}.{s.Method.Name} calls {s.Api}").Distinct().Order().ToList();

        Assert.True(sites.Count > 40, $"the scan found only {sites.Count} API call sites: it is not reading the client");
        Assert.True(violations.Count == 0, "These methods call the API directly. Run the call through the view model's operation runner (a Command, LoadAsync, RunOperationAsync) or mark the hook [RunsInOperation(reason)]:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LoadAsync_IsTheOneLoadTemplate_NoViewModelHidesIt()
    {
        var hidden = ViewModelTypes().Where(t => typeof(ViewModelBase).IsAssignableFrom(t))
            .Where(t => t.GetMethod(nameof(ViewModelBase.LoadAsync), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null)
            .Select(t => t.Name).ToList();

        Assert.Empty(hidden);
    }

    private static string RootNamespace(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        var parts = (type.Namespace ?? string.Empty).Split('.');
        return string.Join('.', parts.Take(Math.Min(parts.Length, 3)));
    }

    private static string Describe(Type type) => type.DeclaringType is { } outer ? $"{Describe(outer)}+{type.Name}" : type.Name;

    /// <summary>True for code that runs inside an operation: a lambda or local function (they are handed to the runner or a command), or a hook the base template calls inside one.</summary>
    private static bool IsOperationCode(Type type, MethodBase method)
    {
        if (IsClosure(type.Name) || IsClosure(method.Name))
        {
            return true;
        }

        var origin = method;
        if (type.IsNested && type.Name.StartsWith('<') && type.Name.Contains(">d__", StringComparison.Ordinal))
        {
            var name = type.Name[1..type.Name.IndexOf('>', StringComparison.Ordinal)];
            origin = type.DeclaringType!.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == name) ?? method;
            if (IsClosure(origin.Name))
            {
                return true;
            }
        }

        return origin is MethodInfo info && HasHookAttribute(info);
    }

    private static bool IsClosure(string name) => name.Contains(">b__", StringComparison.Ordinal) || name.Contains(">g__", StringComparison.Ordinal) || name.Contains(">c__", StringComparison.Ordinal);

    private static bool HasHookAttribute(MethodInfo method)
    {
        for (var current = method; current is not null; current = current.GetBaseDefinition() == current ? null : current.GetBaseDefinition())
        {
            if (current.IsDefined(typeof(RunsInOperationAttribute), false))
            {
                return true;
            }
        }

        return false;
    }

    // The guard that backs the inventory at run time.

    [Fact]
    public async Task AnApiCallMadeOutsideAnOperation_FailsInStrictMode_AndInsideOneItRuns()
    {
        var fake = new Fakes.FakeApiClient();
        var strict = ScopedApiClient.Wrap(fake, strict: true);
        fake.On("GetStockAsync", ApiResult<IReadOnlyList<Contracts.Stock.StockItem>>.Ok([]));

        var outside = await Assert.ThrowsAsync<InvalidOperationException>(() => strict.GetStockAsync());
        Assert.Contains("outside an operation", outside.Message, StringComparison.Ordinal);

        using var runner = new OperationRunner(() => null, () => TimeProvider.System);
        var result = await runner.RunAsync(ct => strict.GetStockAsync(ct));

        Assert.True(result.IsOk);
    }

    [Fact]
    public async Task WarmUpAndLogout_AreTheOnlyCallsAllowedOutsideAnOperation()
    {
        var allowed = typeof(ILifestreamApiClient).GetMethods().Where(ScopedApiClient.IsOutsideOperation).Select(m => m.Name).Order().ToList();

        Assert.Equal(["LogoutAsync", "WarmUpAsync"], allowed);
        await Task.CompletedTask;
    }
}

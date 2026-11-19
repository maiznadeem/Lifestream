using System.Reflection;
using System.Reflection.Emit;
using Lifestream.Client.Api;

namespace Lifestream.Client.Tests;

/// <summary>
/// Finds, by reading the IL of the client assembly, every method that calls a member of <see cref="ILifestreamApiClient"/>. The inventory test uses it to
/// check that no screen code calls the API directly outside the operation runner (SHELL-07).
/// </summary>
public static class ApiCallSiteScanner
{
    private static readonly Dictionary<short, OpCode> OpCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>One place in the client where the API is called.</summary>
    public sealed record CallSite(Type Type, MethodBase Method, string Api);

    public static IReadOnlyList<CallSite> Scan(Assembly assembly)
    {
        var sites = new List<CallSite>();
        foreach (var type in assembly.GetTypes())
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
            {
                foreach (var api in CalledApiMethods(method))
                {
                    sites.Add(new CallSite(type, method, api));
                }
            }
        }

        return sites;
    }

    private static IEnumerable<string> CalledApiMethods(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body?.GetILAsByteArray() is not { } il)
        {
            yield break;
        }

        var module = method.Module;
        Type[]? typeArgs = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        Type[]? methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var i = 0; i < il.Length;)
        {
            var code = il[i] == 0xFE ? OpCodes[(short)(0xFE00 | il[i + 1])] : OpCodes[il[i]];
            i += code.Size;
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, i);
                MethodBase? target = null;
                try
                {
                    target = module.ResolveMethod(token, typeArgs, methodArgs);
                }
                catch (ArgumentException)
                {
                    // A token that cannot be resolved here is not a call into the API interface.
                }

                if (target?.DeclaringType == typeof(ILifestreamApiClient))
                {
                    yield return target.Name;
                }
            }

            i += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
        }
    }
}

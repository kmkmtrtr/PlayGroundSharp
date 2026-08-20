using Microsoft.CodeAnalysis;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.Worker;

/// <summary>Restores compile-time tuple element names that are erased from runtime ValueTuple types.</summary>
internal static class TupleElementNameMapper
{
    public static ResultSnapshot Apply(ResultSnapshot snapshot, ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsTupleType: true } tuple
            ? ApplyTuple(snapshot, tuple)
            : snapshot;

    public static ITypeSymbol? GetAsyncSequenceResultType(ITypeSymbol? type)
    {
        if (type is null) return null;
        var sequenceType = type
            .AllInterfaces
            .Prepend(type)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(static candidate =>
                candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        if (sequenceType?.TypeArguments.SingleOrDefault() is not INamedTypeSymbol awaitable ||
            awaitable.Arity != 1 ||
            awaitable.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks" ||
            awaitable.Name is not ("Task" or "ValueTask"))
            return null;

        return awaitable.TypeArguments[0];
    }

    private static ResultSnapshot ApplyTuple(ResultSnapshot snapshot, INamedTypeSymbol tuple)
    {
        if (snapshot.Properties is null) return snapshot;

        var elements = tuple.TupleElements;
        var properties = new List<ResultProperty>(elements.Length);
        for (var index = 0; index < elements.Length; index++)
        {
            var source = FindPhysicalElement(snapshot, index);
            if (source is null) continue;
            var element = elements[index];
            properties.Add(source with
            {
                Name = element.Name,
                Value = Apply(source.Value, element.Type)
            });
        }

        return snapshot with
        {
            Display = $"{elements.Length} members",
            Properties = properties,
            IsTruncated = snapshot.IsTruncated || properties.Count < elements.Length,
            TotalCount = elements.Length
        };
    }

    private static ResultProperty? FindPhysicalElement(ResultSnapshot snapshot, int logicalIndex)
    {
        if (snapshot.Properties is null) return null;
        if (logicalIndex < 7)
            return snapshot.Properties.FirstOrDefault(property => property.Name == $"Item{logicalIndex + 1}");

        var rest = snapshot.Properties.FirstOrDefault(static property => property.Name == "Rest");
        return rest is null ? null : FindPhysicalElement(rest.Value, logicalIndex - 7);
    }
}

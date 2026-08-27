using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

internal sealed partial class ReadonlyEffectAnalyzer
{
    // Bound the finite partition; large or dynamically sized arrays retain the
    // wildcard abstraction. Unknown indices join all elements of a partition.
    private const int MaxTrackedArrayElements = 256;
    private readonly Dictionary<object, ArrayStorage> _arrays = new(ReferenceEqualityComparer.Instance);

    private sealed class ArrayStorage(int[] lengths, ArrayElement[] elements)
    {
        public int[] Lengths { get; } = lengths;
        public ArrayElement[] Elements { get; } = elements;
        public bool Repeated { get; set; }
    }

    private sealed class ArrayElement(object array, int index)
    {
        public object Array { get; } = array;
        public int Index { get; } = index;
    }

    private void InitializeArrayElements(BoundArrayCreationExpression allocation)
    {
        object root = Root(allocation);
        if (!_arrays.TryGetValue(root, out var array))
        {
            var lengths = new List<int>();
            long count = 1;
            foreach (BoundExpression dimension in allocation.Dimensions)
            {
                if (ConstantIndex(dimension) is not int length || length < 0 || length > MaxTrackedArrayElements) return;
                count *= length;
                if (count > MaxTrackedArrayElements) return;
                lengths.Add(length);
            }
            array = new(lengths.ToArray(), Enumerable.Range(0, (int)count).Select(index => new ArrayElement(root, index)).ToArray());
            _arrays.Add(root, array);
        }
        array.Repeated |= _loopDepth != 0;
        foreach (ArrayElement element in array.Elements)
            StoreValue([element], Read([root], allocation.ElementType), allocation.ElementType);
    }

    private static int? ConstantIndex(BoundExpression expression)
    {
        if (expression is BoundCastExpression cast) return ConstantIndex(cast.Expression);
        if (expression is not BoundLiteralExpression literal) return null;
        return literal.Value switch
        {
            int value => value,
            long value when value is >= int.MinValue and <= int.MaxValue => (int)value,
            ulong value when value <= int.MaxValue => (int)value,
            _ => null,
        };
    }

    private HashSet<object> ArrayElements(IEnumerable<object> arrays, IReadOnlyList<BoundExpression>? indices = null)
    {
        HashSet<object> result = [];
        foreach (object origin in arrays)
        {
            if (!_arrays.TryGetValue(Unwrap(origin), out var array)) { result.Add(origin); continue; }
            int? flat = indices?.Count == array.Lengths.Length ? 0 : null;
            if (flat is not null)
            {
                for (int dimension = 0; dimension < indices!.Count; dimension++)
                {
                    if (ConstantIndex(indices[dimension]) is not int index || index < 0 || index >= array.Lengths[dimension])
                    { flat = null; break; }
                    flat = flat * array.Lengths[dimension] + index;
                }
            }
            if (flat is int element) result.Add(array.Elements[element]);
            else result.UnionWith(array.Elements);
        }
        return result;
    }

    private IEnumerable<object>? ArrayAliasLocations(object location)
    {
        for (object current = Unwrap(location); ;)
        {
            if (current is FieldLocation field) { current = field.Parent; continue; }
            if (current is not ArrayElement element) return null;
            var pending = new Stack<object>(_arrays[element.Array].Elements);
            HashSet<object> result = [];
            while (pending.TryPop(out object? candidate))
            {
                if (!result.Add(candidate)) continue;
                if (_fields.TryGetValue(candidate, out var fields))
                    foreach (object child in fields.Values) pending.Push(child);
            }
            return result;
        }
    }

    private HashSet<object> ArrayCapabilityRange(IEnumerable<object> origins)
    {
        HashSet<object> result = [];
        foreach (object origin in origins)
            if (ArrayAliasLocations(origin) is { } aliases) result.UnionWith(aliases);
            else result.Add(origin);
        return result;
    }
}

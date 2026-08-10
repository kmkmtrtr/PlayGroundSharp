using System.Globalization;
using System.Text;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

internal enum DataTypeInferenceWarning
{
    TruncatedSnapshot,
    FallbackType,
    EmptyCollection
}

internal sealed record DataTypeInferenceResult(
    string RootTypeName,
    string VariableName,
    string TargetType,
    string GeneratedCode,
    IReadOnlyList<DataTypeInferenceWarning> Warnings);

/// <summary>Infers portable C# data models from process-neutral result snapshots.</summary>
internal static class DataTypeInference
{
    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while", "add", "alias", "and", "ascending", "async", "await", "by",
        "descending", "dynamic", "equals", "file", "from", "get", "global", "group", "init", "into",
        "join", "let", "managed", "nameof", "nint", "not", "notnull", "nuint", "on", "or", "orderby",
        "partial", "record", "remove", "required", "scoped", "select", "set", "unmanaged", "value",
        "var", "when", "where", "with", "yield"
    };

    public static bool CanInfer(ResultSnapshot snapshot) =>
        TryInferRoot(snapshot, new HashSet<DataTypeInferenceWarning>(), out _, out _);

    public static string SuggestTypeName(string sourceName, ResultSnapshot snapshot)
    {
        var basis = NormalizeSourceName(sourceName);
        var identifier = CreateIdentifier(basis);
        return IsSequence(snapshot) ? Singularize(identifier) + "Item" : identifier + "Model";
    }

    public static string SuggestVariableName(string sourceName)
    {
        var basis = NormalizeSourceName(sourceName);
        var identifier = CreateIdentifier(basis);
        return "typed" + identifier;
    }

    public static DataTypeInferenceResult? Generate(
        ResultSnapshot snapshot,
        string sourceExpression,
        string rootTypeName,
        string variableName)
    {
        rootTypeName = rootTypeName.Trim();
        variableName = variableName.Trim();
        if (!IsIdentifier(rootTypeName) || !RetainedResultStatement.IsValidVariableName(variableName)) return null;

        var warnings = new HashSet<DataTypeInferenceWarning>();
        if (!TryInferRoot(snapshot, warnings, out var rootObject, out var rootIsSequence)) return null;

        var renderer = new ModelRenderer(rootTypeName.TrimStart('@'));
        var definitions = renderer.Render(rootObject!);
        var itemType = renderer.GetTypeName(rootObject!);
        var targetType = rootIsSequence
            ? $"List<{itemType}{(rootObject!.Nullable ? "?" : string.Empty)}>"
            : itemType;
        var codeTargetType = rootIsSequence
            ? $"global::System.Collections.Generic.List<{itemType}{(rootObject!.Nullable ? "?" : string.Empty)}>"
            : itemType;
        var code = new StringBuilder()
            .Append("var ").Append(variableName).Append(" = global::System.Text.Json.JsonSerializer.Deserialize<")
            .Append(codeTargetType).AppendLine(">(")
            .Append("    global::System.Text.Json.JsonSerializer.Serialize(").Append(sourceExpression).AppendLine("))!;")
            .AppendLine()
            .Append(definitions)
            .ToString();
        return new(rootTypeName, variableName, targetType, code, warnings.Order().ToArray());
    }

    private static bool TryInferRoot(
        ResultSnapshot snapshot,
        HashSet<DataTypeInferenceWarning> warnings,
        out Shape? rootObject,
        out bool rootIsSequence)
    {
        var root = Infer(snapshot, warnings);
        if (ContainsUnresolvedNull(root)) warnings.Add(DataTypeInferenceWarning.FallbackType);
        rootIsSequence = root.Kind == ShapeKind.Array;
        rootObject = rootIsSequence ? root.Element : root;
        return rootObject?.Kind == ShapeKind.Object;
    }

    private static bool ContainsUnresolvedNull(Shape shape) =>
        shape.Kind == ShapeKind.Null ||
        shape.Properties?.Any(property => ContainsUnresolvedNull(property.Type)) == true ||
        shape.Element is not null && ContainsUnresolvedNull(shape.Element);

    private static Shape Infer(ResultSnapshot snapshot, HashSet<DataTypeInferenceWarning> warnings)
    {
        if (snapshot.IsTruncated)
            warnings.Add(DataTypeInferenceWarning.TruncatedSnapshot);
        if (snapshot.Kind is SnapshotKind.MaxDepth or SnapshotKind.Circular or SnapshotKind.Exception)
        {
            warnings.Add(DataTypeInferenceWarning.FallbackType);
            return Shape.Fallback();
        }
        if (snapshot.Kind == SnapshotKind.Null) return Shape.Null();
        if (snapshot.Properties is not null && snapshot.Kind is SnapshotKind.Json or SnapshotKind.Object)
            return Shape.Object(snapshot.Properties.Select(property =>
                new ShapeProperty(property.Name, Infer(property.Value, warnings))).ToArray());
        if (snapshot.Items is not null && snapshot.Kind is SnapshotKind.Json or SnapshotKind.Sequence)
        {
            if (snapshot.Items.Count == 0)
            {
                warnings.Add(DataTypeInferenceWarning.EmptyCollection);
                return Shape.Array(Shape.Unknown());
            }
            var element = snapshot.Items
                .Select(item => Infer(item, warnings))
                .Aggregate((left, right) => Merge(left, right, warnings));
            return Shape.Array(element);
        }

        return snapshot.Kind switch
        {
            SnapshotKind.Boolean => Shape.Scalar("bool"),
            SnapshotKind.String => Shape.Scalar("string"),
            SnapshotKind.DateTime => Shape.Scalar("DateTime"),
            SnapshotKind.Guid => Shape.Scalar("Guid"),
            SnapshotKind.Number => Shape.Scalar(InferNumberType(snapshot)),
            SnapshotKind.Enum => Shape.Scalar("string"),
            _ => Fallback(warnings)
        };
    }

    private static Shape Fallback(HashSet<DataTypeInferenceWarning> warnings)
    {
        warnings.Add(DataTypeInferenceWarning.FallbackType);
        return Shape.Fallback();
    }

    private static Shape Merge(
        Shape left,
        Shape right,
        HashSet<DataTypeInferenceWarning> warnings)
    {
        if (left.Kind == ShapeKind.Null) return right.WithNullable();
        if (right.Kind == ShapeKind.Null) return left.WithNullable();
        if (left.Kind == ShapeKind.Unknown) return right.WithNullable();
        if (right.Kind == ShapeKind.Unknown) return left.WithNullable();
        var nullable = left.Nullable || right.Nullable;

        if (left.Kind == ShapeKind.Scalar && right.Kind == ShapeKind.Scalar)
        {
            if (left.ScalarType == right.ScalarType)
                return Shape.Scalar(left.ScalarType!).WithNullable(nullable);
            if (TryMergeNumbers(left.ScalarType!, right.ScalarType!, out var numberType))
                return Shape.Scalar(numberType).WithNullable(nullable);
            return Shape.Fallback(nullable, warnings);
        }
        if (left.Kind == ShapeKind.Array && right.Kind == ShapeKind.Array)
            return Shape.Array(Merge(left.Element!, right.Element!, warnings)).WithNullable(nullable);
        if (left.Kind == ShapeKind.Object && right.Kind == ShapeKind.Object)
        {
            var rightByName = right.Properties!.ToDictionary(property => property.JsonName, StringComparer.Ordinal);
            var merged = new List<ShapeProperty>();
            foreach (var property in left.Properties!)
            {
                if (rightByName.Remove(property.JsonName, out var matching))
                    merged.Add(new(property.JsonName, Merge(property.Type, matching.Type, warnings)));
                else
                    merged.Add(new(property.JsonName, property.Type.WithNullable()));
            }
            merged.AddRange(right.Properties!
                .Where(property => rightByName.ContainsKey(property.JsonName))
                .Select(property => new ShapeProperty(property.JsonName, property.Type.WithNullable())));
            return Shape.Object(merged).WithNullable(nullable);
        }
        if (left.Kind == ShapeKind.Fallback && right.Kind == ShapeKind.Fallback)
            return Shape.Fallback(nullable);
        return Shape.Fallback(nullable, warnings);
    }

    private static string InferNumberType(ResultSnapshot snapshot)
    {
        var known = snapshot.TypeName switch
        {
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" => "int",
            "System.UInt32" or "System.Int64" => "long",
            "System.UInt64" or "System.Decimal" => "decimal",
            "System.Single" or "System.Double" => "double",
            _ => null
        };
        if (known is not null) return known;
        var value = snapshot.Display ?? string.Empty;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return "int";
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return "long";
        if (!value.Contains('e', StringComparison.OrdinalIgnoreCase) &&
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) return "decimal";
        return "double";
    }

    private static bool TryMergeNumbers(string left, string right, out string type)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["int"] = 0, ["long"] = 1, ["decimal"] = 2, ["double"] = 3
        };
        if (!rank.TryGetValue(left, out var leftRank) || !rank.TryGetValue(right, out var rightRank))
        {
            type = string.Empty;
            return false;
        }
        type = rank.Single(pair => pair.Value == Math.Max(leftRank, rightRank)).Key;
        return true;
    }

    private static bool IsSequence(ResultSnapshot snapshot) =>
        snapshot.Items is not null && snapshot.Kind is SnapshotKind.Json or SnapshotKind.Sequence;

    private static string NormalizeSourceName(string sourceName)
    {
        if (sourceName.StartsWith("Out[", StringComparison.Ordinal)) return "Result";
        return sourceName.TrimStart('@');
    }

    private static bool IsIdentifier(string value) =>
        RetainedResultStatement.IsValidVariableName(value) && !value.StartsWith('@');

    private static string CreateIdentifier(string value)
    {
        var result = new StringBuilder();
        var capitalize = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character != '_')
            {
                capitalize = true;
                continue;
            }
            if (character == '_')
            {
                capitalize = true;
                continue;
            }
            result.Append(capitalize ? char.ToUpperInvariant(character) : character);
            capitalize = false;
        }
        if (result.Length == 0) result.Append("Value");
        if (!char.IsLetter(result[0]) && result[0] != '_') result.Insert(0, "Value");
        if (CSharpKeywords.Contains(result.ToString())) result.Append("Value");
        return result.ToString();
    }

    private static string Singularize(string value)
    {
        if (value.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && value.Length > 3)
            return value[..^3] + "y";
        if (value.EndsWith('s') && !value.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && value.Length > 1)
            return value[..^1];
        return value;
    }

    private static string EscapeString(string value) => '"' + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal) + '"';

    private enum ShapeKind { Null, Scalar, Object, Array, Fallback, Unknown }

    private sealed class Shape
    {
        private Shape(
            ShapeKind kind,
            string? scalarType = null,
            IReadOnlyList<ShapeProperty>? properties = null,
            Shape? element = null,
            bool nullable = false)
        {
            Kind = kind;
            ScalarType = scalarType;
            Properties = properties;
            Element = element;
            Nullable = nullable;
        }

        public ShapeKind Kind { get; }
        public string? ScalarType { get; }
        public IReadOnlyList<ShapeProperty>? Properties { get; }
        public Shape? Element { get; }
        public bool Nullable { get; }

        public static Shape Null() => new(ShapeKind.Null, nullable: true);
        public static Shape Unknown() => new(ShapeKind.Unknown, nullable: true);
        public static Shape Scalar(string type) => new(ShapeKind.Scalar, scalarType: type);
        public static Shape Object(IEnumerable<ShapeProperty> properties) =>
            new(ShapeKind.Object, properties: properties.ToArray());
        public static Shape Array(Shape element) => new(ShapeKind.Array, element: element);
        public static Shape Fallback(bool nullable = false) => new(ShapeKind.Fallback, nullable: nullable);
        public static Shape Fallback(bool nullable, HashSet<DataTypeInferenceWarning> warnings)
        {
            warnings.Add(DataTypeInferenceWarning.FallbackType);
            return Fallback(nullable);
        }

        public Shape WithNullable(bool nullable = true) => nullable == Nullable
            ? this
            : new(Kind, ScalarType, Properties, Element, nullable);
    }

    private sealed record ShapeProperty(string JsonName, Shape Type);

    private sealed class ModelRenderer
    {
        private readonly string rootTypeName;
        private readonly Dictionary<Shape, string> typeNames = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> usedTypeNames = new(StringComparer.Ordinal);

        public ModelRenderer(string rootTypeName) => this.rootTypeName = rootTypeName;

        public string GetTypeName(Shape shape) => typeNames[shape];

        public string Render(Shape root)
        {
            AssignNames(root, rootTypeName);
            var result = new StringBuilder();
            foreach (var (shape, typeName) in typeNames)
            {
                result.Append("public sealed class ").AppendLine(typeName)
                    .AppendLine("{");
                var usedProperties = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in shape.Properties!)
                {
                    var propertyName = MakeUnique(CreateIdentifier(property.JsonName), usedProperties);
                    if (!string.Equals(property.JsonName, propertyName, StringComparison.Ordinal))
                        result.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(")
                            .Append(EscapeString(property.JsonName)).AppendLine(")]");
                    var typeText = RenderType(property.Type);
                    result.Append("    public ").Append(typeText).Append(' ').Append(propertyName)
                        .Append(" { get; init; }").AppendLine(GetInitializer(property.Type));
                }
                result.AppendLine("}").AppendLine();
            }
            return result.ToString().TrimEnd();
        }

        private void AssignNames(Shape shape, string proposedName)
        {
            if (shape.Kind != ShapeKind.Object || typeNames.ContainsKey(shape)) return;
            var name = MakeUnique(CreateIdentifier(proposedName), usedTypeNames);
            typeNames.Add(shape, name);
            foreach (var property in shape.Properties!)
            {
                if (property.Type.Kind == ShapeKind.Object)
                    AssignNames(property.Type, CreateIdentifier(property.JsonName));
                else if (property.Type.Kind == ShapeKind.Array && property.Type.Element?.Kind == ShapeKind.Object)
                    AssignNames(property.Type.Element, Singularize(CreateIdentifier(property.JsonName)) + "Item");
            }
        }

        private string RenderType(Shape shape)
        {
            var baseType = shape.Kind switch
            {
                ShapeKind.Scalar => shape.ScalarType!,
                ShapeKind.Object => GetTypeName(shape),
                ShapeKind.Array => $"global::System.Collections.Generic.List<{RenderType(shape.Element!)}>",
                _ => "global::System.Text.Json.Nodes.JsonNode"
            };
            return shape.Nullable ? baseType + "?" : baseType;
        }

        private static string GetInitializer(Shape shape)
        {
            if (shape.Nullable || shape.Kind == ShapeKind.Scalar && shape.ScalarType != "string") return string.Empty;
            return shape.Kind switch
            {
                ShapeKind.Scalar => " = string.Empty;",
                ShapeKind.Object or ShapeKind.Array => " = new();",
                _ => " = null!;"
            };
        }

        private static string MakeUnique(string proposed, HashSet<string> used)
        {
            var value = proposed;
            var suffix = 2;
            while (!used.Add(value)) value = proposed + suffix++;
            return value;
        }
    }
}

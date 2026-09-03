using System.Globalization;
using System.Text;
using PlayGroundSharp.Core;
using PlayGroundSharp.LanguageService;

namespace PlayGroundSharp.App;

internal enum DataTypeInferenceWarning
{
    TruncatedSnapshot,
    FallbackType,
    EmptyCollection,
    UnreadableProperty
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
        TryInferRoot(
            snapshot,
            new HashSet<DataTypeInferenceWarning>(),
            snapshot.Kind != SnapshotKind.Json,
            out _,
            out _);

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
        var useDirectClrProjection = snapshot.Kind != SnapshotKind.Json;
        if (!TryInferRoot(
                snapshot,
                warnings,
                useDirectClrProjection,
                out var rootObject,
                out var rootIsSequence)) return null;

        var renderer = new ModelRenderer(rootTypeName.TrimStart('@'));
        var definitions = renderer.Render(rootObject!);
        var itemType = renderer.GetTypeName(rootObject!);
        var targetType = rootIsSequence
            ? $"List<{itemType}{(rootObject!.Nullable ? "?" : string.Empty)}>"
            : itemType;
        var codeTargetType = rootIsSequence
            ? $"global::System.Collections.Generic.List<{itemType}{(rootObject!.Nullable ? "?" : string.Empty)}>"
            : itemType;
        var code = useDirectClrProjection
            ? DirectProjectionRenderer.Render(
                renderer,
                rootObject!,
                rootIsSequence,
                sourceExpression,
                codeTargetType,
                variableName,
                definitions)
            : new StringBuilder()
                .Append(codeTargetType).Append(' ').Append(variableName)
                .Append(" = global::System.Text.Json.JsonSerializer.Deserialize<")
                .Append(codeTargetType).AppendLine(">(")
                .Append("    global::System.Text.Json.JsonSerializer.Serialize((object?)(")
                .Append(sourceExpression).AppendLine(")))!;")
                .AppendLine()
                .Append(definitions)
                .ToString();
        return new(rootTypeName, variableName, targetType, code, warnings.Order().ToArray());
    }

    private static bool TryInferRoot(
        ResultSnapshot snapshot,
        HashSet<DataTypeInferenceWarning> warnings,
        bool preserveClrTypes,
        out Shape? rootObject,
        out bool rootIsSequence)
    {
        var root = Infer(snapshot, warnings, preserveClrTypes, expandObject: true);
        if (ContainsUnresolvedNull(root)) warnings.Add(DataTypeInferenceWarning.FallbackType);
        rootIsSequence = root.Kind == ShapeKind.Array;
        rootObject = rootIsSequence ? root.Element : root;
        return rootObject?.Kind == ShapeKind.Object;
    }

    private static bool ContainsUnresolvedNull(Shape shape) =>
        shape.Kind == ShapeKind.Null ||
        shape.Properties?.Any(property => ContainsUnresolvedNull(property.Type)) == true ||
        shape.Element is not null && ContainsUnresolvedNull(shape.Element);

    private static Shape Infer(
        ResultSnapshot snapshot,
        HashSet<DataTypeInferenceWarning> warnings,
        bool preserveClrTypes,
        bool expandObject)
    {
        if (snapshot.IsInferredNullable)
            return Infer(
                snapshot with { IsInferredNullable = false },
                warnings,
                preserveClrTypes,
                expandObject).WithNullable();
        if (snapshot.IsTruncated)
            warnings.Add(DataTypeInferenceWarning.TruncatedSnapshot);
        if (snapshot.Kind is SnapshotKind.MaxDepth or SnapshotKind.Circular or SnapshotKind.Exception)
        {
            warnings.Add(DataTypeInferenceWarning.FallbackType);
            return preserveClrTypes ? Shape.Scalar("object", isReferenceType: true).WithNullable() : Shape.Fallback();
        }
        if (snapshot.Kind == SnapshotKind.Null) return Shape.Null();
        if (preserveClrTypes && !expandObject && snapshot.TypeExpression is { Length: > 0 } runtimeType)
            return Shape.Scalar(runtimeType, snapshot.IsReferenceType ?? true);
        if (snapshot.Properties is not null && snapshot.Kind is SnapshotKind.Json or SnapshotKind.Object)
        {
            var properties = new List<ShapeProperty>();
            var propertyIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var property in snapshot.Properties)
            {
                if (preserveClrTypes && (!property.IsReadable || property.Value.Kind == SnapshotKind.Exception))
                {
                    warnings.Add(DataTypeInferenceWarning.UnreadableProperty);
                    continue;
                }

                Shape propertyShape;
                if (preserveClrTypes &&
                    property.Value.Kind == SnapshotKind.Null &&
                    property.DeclaredTypeExpression is { Length: > 0 } declaredType)
                {
                    propertyShape = Shape
                        .Scalar(declaredType, property.DeclaredTypeIsReferenceType ?? true)
                        .WithNullable();
                }
                else if (preserveClrTypes &&
                         (property.Value.TypeExpression ?? property.DeclaredTypeExpression) is { Length: > 0 } clrType)
                {
                    propertyShape = Shape.Scalar(
                        clrType,
                        property.Value.IsReferenceType ?? property.DeclaredTypeIsReferenceType ?? true);
                }
                else
                {
                    propertyShape = Infer(property.Value, warnings, preserveClrTypes, expandObject: false);
                }
                if (property.Value.IsInferredNullable) propertyShape = propertyShape.WithNullable();
                if (property.IsOptional) propertyShape = propertyShape.WithNullable();
                var inferredProperty = new ShapeProperty(property.Name, propertyShape);
                if (propertyIndexes.TryGetValue(property.Name, out var existingIndex))
                    properties[existingIndex] = new(
                        property.Name,
                        Merge(properties[existingIndex].Type, propertyShape, warnings));
                else
                {
                    propertyIndexes.Add(property.Name, properties.Count);
                    properties.Add(inferredProperty);
                }
            }
            return Shape.Object(properties);
        }
        if (snapshot.Items is not null && snapshot.Kind is SnapshotKind.Json or SnapshotKind.Sequence)
        {
            if (snapshot.Items.Count == 0)
            {
                warnings.Add(DataTypeInferenceWarning.EmptyCollection);
                return Shape.Array(Shape.Unknown());
            }
            var element = snapshot.Items
                .Select(item => Infer(item, warnings, preserveClrTypes, expandObject))
                .Aggregate((left, right) => Merge(left, right, warnings));
            return Shape.Array(element);
        }

        return snapshot.Kind switch
        {
            SnapshotKind.Boolean => Shape.Scalar("bool"),
            SnapshotKind.String => Shape.Scalar("string", isReferenceType: true),
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
                return Shape.Scalar(left.ScalarType!, left.IsReferenceType).WithNullable(nullable);
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
            bool nullable = false,
            bool isReferenceType = false)
        {
            Kind = kind;
            ScalarType = scalarType;
            Properties = properties;
            Element = element;
            Nullable = nullable;
            IsReferenceType = isReferenceType;
        }

        public ShapeKind Kind { get; }
        public string? ScalarType { get; }
        public IReadOnlyList<ShapeProperty>? Properties { get; }
        public Shape? Element { get; }
        public bool Nullable { get; }
        public bool IsReferenceType { get; }

        public static Shape Null() => new(ShapeKind.Null, nullable: true);
        public static Shape Unknown() => new(ShapeKind.Unknown, nullable: true);
        public static Shape Scalar(string type, bool isReferenceType = false) =>
            new(ShapeKind.Scalar, scalarType: type, isReferenceType: isReferenceType);
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
            : new(Kind, ScalarType, Properties, Element, nullable, IsReferenceType);
    }

    private sealed record ShapeProperty(string JsonName, Shape Type);

    private sealed class ModelRenderer
    {
        private readonly string rootTypeName;
        private readonly Dictionary<Shape, string> typeNames = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ShapeProperty, string> propertyNames = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> usedTypeNames = new(StringComparer.Ordinal);

        public ModelRenderer(string rootTypeName) => this.rootTypeName = rootTypeName;

        public string GetTypeName(Shape shape) => typeNames[shape];
        public string GetPropertyName(ShapeProperty property) => propertyNames[property];
        public IEnumerable<Shape> ObjectShapes => typeNames.Keys;

        public string Render(Shape root)
        {
            AssignNames(root, rootTypeName);
            var result = new StringBuilder();
            foreach (var (shape, typeName) in typeNames)
            {
                result.Append("public sealed class ").AppendLine(typeName)
                    .AppendLine("{");
                // C# forbids a member whose name is the same as its containing type
                // (CS0542). Reserve the generated type name before assigning members.
                var usedProperties = new HashSet<string>(StringComparer.Ordinal) { typeName };
                foreach (var property in shape.Properties!)
                {
                    var propertyName = MakeUnique(CreateIdentifier(property.JsonName), usedProperties);
                    propertyNames[property] = propertyName;
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

        public string RenderType(Shape shape)
        {
            var baseType = shape.Kind switch
            {
                ShapeKind.Scalar => shape.ScalarType!,
                ShapeKind.Object => GetTypeName(shape),
                ShapeKind.Array => $"global::System.Collections.Generic.List<{RenderType(shape.Element!)}>",
                _ => "global::System.Text.Json.Nodes.JsonNode"
            };
            return shape.Nullable && !baseType.EndsWith("?", StringComparison.Ordinal) ? baseType + "?" : baseType;
        }

        private static string GetInitializer(Shape shape)
        {
            if (shape.Nullable || shape.Kind == ShapeKind.Scalar && !shape.IsReferenceType) return string.Empty;
            return shape.Kind switch
            {
                ShapeKind.Scalar when shape.ScalarType == "string" => " = string.Empty;",
                ShapeKind.Scalar => " = null!;",
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

    private static class DirectProjectionRenderer
    {
        public static string Render(
            ModelRenderer renderer,
            Shape root,
            bool rootIsSequence,
            string sourceExpression,
            string targetType,
            string variableName,
            string definitions)
        {
            var suffix = CreateIdentifier(renderer.GetTypeName(root));
            var mapName = $"__Map{suffix}";
            var mapSequenceName = $"__Map{suffix}Sequence";
            var readName = $"__Read{suffix}Member";
            var convertName = $"__Convert{suffix}Value";
            var result = new StringBuilder()
                .Append(targetType).Append(' ').Append(variableName).Append(" = ");
            if (rootIsSequence)
            {
                result.Append(mapSequenceName).Append("<").Append(renderer.RenderType(root)).Append(">((object?)(")
                    .Append(sourceExpression).Append("), static item => ").Append(mapName).AppendLine("(item));");
            }
            else
            {
                result.Append(mapName).Append("((object?)(").Append(sourceExpression).AppendLine("));");
            }
            result.AppendLine();

            foreach (var shape in renderer.ObjectShapes)
            {
                var currentMapName = $"__Map{CreateIdentifier(renderer.GetTypeName(shape))}";
                result.Append("static ").Append(renderer.GetTypeName(shape)).Append(' ').Append(currentMapName)
                    .AppendLine("(object? source) => new()")
                    .AppendLine("{");
                foreach (var property in shape.Properties!)
                {
                    result.Append("    ").Append(renderer.GetPropertyName(property)).Append(" = ")
                        .Append(RenderValue(
                            renderer,
                            property.Type,
                            $"{readName}(source, {EscapeString(property.JsonName)})",
                            mapSequenceName,
                            convertName))
                        .AppendLine(",");
                }
                result.AppendLine("};").AppendLine();
            }

            result.Append("static object? ").Append(readName).AppendLine("(object? source, string name)")
                .AppendLine("{")
                .AppendLine("    if (source is null) return null;")
                .AppendLine("    try")
                .AppendLine("    {")
                .AppendLine("        if (source is global::System.Text.Json.JsonElement element)")
                .AppendLine("            return element.ValueKind == global::System.Text.Json.JsonValueKind.Object && element.TryGetProperty(name, out var jsonValue) ? jsonValue : null;")
                .AppendLine("        if (source is global::System.Collections.Generic.IDictionary<string, object?> values)")
                .AppendLine("            return values.TryGetValue(name, out var value) ? value : null;")
                .AppendLine("        if (source is global::System.Collections.Generic.IReadOnlyDictionary<string, object?> readOnlyValues)")
                .AppendLine("            return readOnlyValues.TryGetValue(name, out var value) ? value : null;")
                .AppendLine("        if (source is global::System.Collections.IDictionary dictionary)")
                .AppendLine("            return dictionary.Contains(name) ? dictionary[name] : null;")
                .AppendLine("        var type = source.GetType();")
                .AppendLine("        var property = type.GetProperty(name, global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public);")
                .AppendLine("        if (property is not null && property.GetIndexParameters().Length == 0) return property.GetValue(source);")
                .AppendLine("        var field = type.GetField(name, global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public);")
                .AppendLine("        if (field is not null) return field.GetValue(source);")
                .AppendLine("        var indexer = type.GetProperty(\"Item\", [typeof(string)]);")
                .AppendLine("        return indexer?.GetValue(source, [name]);")
                .AppendLine("    }")
                .AppendLine("    catch")
                .AppendLine("    {")
                .AppendLine("        return null;")
                .AppendLine("    }")
                .AppendLine("}")
                .AppendLine()
                .Append("static T ").Append(convertName).AppendLine("<T>(object? value)")
                .AppendLine("{")
                .AppendLine("    if (value is null or global::System.DBNull) return default!;")
                .AppendLine("    if (value is T typed) return typed;")
                .AppendLine("    if (value is global::System.Text.Json.Nodes.JsonNode node)")
                .AppendLine("        return node.Deserialize<T>()!;")
                .AppendLine("    if (value is global::System.Text.Json.JsonElement element)")
                .AppendLine("        return element.Deserialize<T>()!;")
                .AppendLine("    return (T)value;")
                .AppendLine("}")
                .AppendLine()
                .Append("static global::System.Collections.Generic.List<T> ").Append(mapSequenceName)
                .AppendLine("<T>(object? source, global::System.Func<object?, T> map)")
                .AppendLine("{")
                .AppendLine("    if (source is global::System.Data.DataTable table) source = table.Rows;")
                .AppendLine("    if (source is not global::System.Collections.IEnumerable values) return [];")
                .AppendLine("    return global::System.Linq.Enumerable.ToList(")
                .AppendLine("        global::System.Linq.Enumerable.Select(")
                .AppendLine("            global::System.Linq.Enumerable.Cast<object?>(values), map));")
                .AppendLine("}")
                .AppendLine()
                .Append(definitions);
            return result.ToString();
        }

        private static string RenderValue(
            ModelRenderer renderer,
            Shape shape,
            string source,
            string mapSequenceName,
            string convertName) => shape.Kind switch
        {
            ShapeKind.Object => $"__Map{CreateIdentifier(renderer.GetTypeName(shape))}({source})",
            ShapeKind.Array => $"{mapSequenceName}<{renderer.RenderType(shape.Element!)}>({source}, static item => {RenderValue(renderer, shape.Element!, "item", mapSequenceName, convertName)})",
            _ => $"{convertName}<{renderer.RenderType(shape)}>({source})"
        };
    }
}

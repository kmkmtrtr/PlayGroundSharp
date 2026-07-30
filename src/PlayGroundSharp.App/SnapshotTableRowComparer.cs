using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

internal enum SnapshotTableSortMode
{
    NaturalText,
    Number
}

internal sealed class SnapshotTableRowComparer : IComparer
{
    private readonly int columnIndex;
    private readonly ListSortDirection direction;

    private SnapshotTableRowComparer(
        int columnIndex,
        ListSortDirection direction,
        SnapshotTableSortMode mode)
    {
        this.columnIndex = columnIndex;
        this.direction = direction;
        Mode = mode;
    }

    public SnapshotTableSortMode Mode { get; }

    public static SnapshotTableRowComparer Create(
        IReadOnlyList<SnapshotTableRow> rows,
        int columnIndex,
        ListSortDirection direction)
    {
        var comparableCells = rows
            .Select(row => GetCell(row, columnIndex))
            .Where(static cell => cell?.Source?.Kind is not null and not SnapshotKind.Null)
            .ToArray();
        var mode = comparableCells.Length > 0 &&
                   comparableCells.All(static cell => cell!.Source!.Kind == SnapshotKind.Number)
            ? SnapshotTableSortMode.Number
            : SnapshotTableSortMode.NaturalText;
        return new(columnIndex, direction, mode);
    }

    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is not SnapshotTableRow left) return y is SnapshotTableRow ? -1 : 0;
        if (y is not SnapshotTableRow right) return 1;

        var leftCell = GetCell(left, columnIndex);
        var rightCell = GetCell(right, columnIndex);
        var leftIsEmpty = IsEmpty(leftCell);
        var rightIsEmpty = IsEmpty(rightCell);
        if (leftIsEmpty != rightIsEmpty) return leftIsEmpty ? 1 : -1;
        if (leftIsEmpty) return left.SourceIndex.CompareTo(right.SourceIndex);

        var comparison = Mode == SnapshotTableSortMode.Number
            ? CompareNumbers(leftCell!.Source!.Display, rightCell!.Source!.Display)
            : CompareNaturally(leftCell!.Display, rightCell!.Display);
        if (comparison != 0)
            return direction == ListSortDirection.Ascending ? comparison : -comparison;

        return left.SourceIndex.CompareTo(right.SourceIndex);
    }

    internal static int CompareNaturally(string? left, string? right)
    {
        left ??= string.Empty;
        right ??= string.Empty;
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftIsDigit = char.IsAsciiDigit(left[leftIndex]);
            var rightIsDigit = char.IsAsciiDigit(right[rightIndex]);
            int comparison;
            if (leftIsDigit && rightIsDigit)
            {
                comparison = CompareDigitRuns(left, ref leftIndex, right, ref rightIndex);
            }
            else
            {
                var leftStart = leftIndex;
                var rightStart = rightIndex;
                while (leftIndex < left.Length && !char.IsAsciiDigit(left[leftIndex])) leftIndex++;
                while (rightIndex < right.Length && !char.IsAsciiDigit(right[rightIndex])) rightIndex++;
                comparison = StringComparer.OrdinalIgnoreCase.Compare(
                    left[leftStart..leftIndex],
                    right[rightStart..rightIndex]);
            }
            if (comparison != 0) return comparison;
        }

        var lengthComparison = (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        return lengthComparison != 0
            ? lengthComparison
            : StringComparer.Ordinal.Compare(left, right);
    }

    internal static int CompareNumbers(string? left, string? right)
    {
        if (ArbitraryDecimal.TryParse(left, out var leftNumber) &&
            ArbitraryDecimal.TryParse(right, out var rightNumber))
            return leftNumber.CompareTo(rightNumber);

        if (double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftSpecial) &&
            double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rightSpecial))
        {
            var rankComparison = SpecialNumberRank(leftSpecial).CompareTo(
                SpecialNumberRank(rightSpecial));
            return rankComparison != 0 ? rankComparison : leftSpecial.CompareTo(rightSpecial);
        }

        return CompareNaturally(left, right);
    }

    private static SnapshotTableCell? GetCell(SnapshotTableRow row, int index) =>
        index >= 0 && index < row.Cells.Count ? row.Cells[index] : null;

    private static bool IsEmpty(SnapshotTableCell? cell) =>
        cell?.Source is null || cell.Source.Kind == SnapshotKind.Null;

    private static int SpecialNumberRank(double value) =>
        double.IsNegativeInfinity(value) ? 0 :
        double.IsFinite(value) ? 1 :
        double.IsPositiveInfinity(value) ? 2 :
        3;

    private static int CompareDigitRuns(
        string left,
        ref int leftIndex,
        string right,
        ref int rightIndex)
    {
        var leftStart = leftIndex;
        var rightStart = rightIndex;
        while (leftIndex < left.Length && char.IsAsciiDigit(left[leftIndex])) leftIndex++;
        while (rightIndex < right.Length && char.IsAsciiDigit(right[rightIndex])) rightIndex++;

        var leftSignificant = leftStart;
        var rightSignificant = rightStart;
        while (leftSignificant < leftIndex && left[leftSignificant] == '0') leftSignificant++;
        while (rightSignificant < rightIndex && right[rightSignificant] == '0') rightSignificant++;

        var leftLength = leftIndex - leftSignificant;
        var rightLength = rightIndex - rightSignificant;
        var lengthComparison = leftLength.CompareTo(rightLength);
        if (lengthComparison != 0) return lengthComparison;

        var digitComparison = left.AsSpan(leftSignificant, leftLength)
            .SequenceCompareTo(right.AsSpan(rightSignificant, rightLength));
        if (digitComparison != 0) return digitComparison;

        return (leftIndex - leftStart).CompareTo(rightIndex - rightStart);
    }

    private readonly record struct ArbitraryDecimal(
        int Sign,
        string Digits,
        BigInteger DecimalPosition) : IComparable<ArbitraryDecimal>
    {
        public int CompareTo(ArbitraryDecimal other)
        {
            var signComparison = Sign.CompareTo(other.Sign);
            if (signComparison != 0) return signComparison;
            if (Sign == 0) return 0;

            var magnitudeComparison = DecimalPosition.CompareTo(other.DecimalPosition);
            if (magnitudeComparison == 0)
            {
                var length = Math.Max(Digits.Length, other.Digits.Length);
                for (var index = 0; index < length; index++)
                {
                    var leftDigit = index < Digits.Length ? Digits[index] : '0';
                    var rightDigit = index < other.Digits.Length ? other.Digits[index] : '0';
                    magnitudeComparison = leftDigit.CompareTo(rightDigit);
                    if (magnitudeComparison != 0) break;
                }
            }
            return Sign > 0 ? magnitudeComparison : -magnitudeComparison;
        }

        public static bool TryParse(string? text, out ArbitraryDecimal value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var span = text.AsSpan().Trim();
            var sign = 1;
            if (span[0] is '+' or '-')
            {
                if (span[0] == '-') sign = -1;
                span = span[1..];
            }
            if (span.IsEmpty) return false;

            var exponent = BigInteger.Zero;
            var exponentIndex = span.IndexOfAny('e', 'E');
            if (exponentIndex >= 0)
            {
                if (!BigInteger.TryParse(span[(exponentIndex + 1)..], out exponent))
                    return false;
                span = span[..exponentIndex];
            }

            var decimalIndex = span.IndexOf('.');
            if (decimalIndex >= 0 && span[(decimalIndex + 1)..].Contains('.'))
                return false;
            var digitsBeforeDecimal = decimalIndex >= 0 ? decimalIndex : span.Length;
            var digits = decimalIndex >= 0
                ? string.Concat(span[..decimalIndex], span[(decimalIndex + 1)..])
                : span.ToString();
            if (digits.Length == 0 || digits.Any(static character => !char.IsAsciiDigit(character)))
                return false;

            var leadingZeros = 0;
            while (leadingZeros < digits.Length && digits[leadingZeros] == '0') leadingZeros++;
            if (leadingZeros == digits.Length)
            {
                value = new(0, string.Empty, BigInteger.Zero);
                return true;
            }

            digits = digits[leadingZeros..].TrimEnd('0');
            value = new(
                sign,
                digits,
                new BigInteger(digitsBeforeDecimal - leadingZeros) + exponent);
            return true;
        }
    }
}

namespace PlayGroundSharp.App;

internal sealed class TableColumnLayout
{
    private readonly int columnCount;
    private readonly List<int> order;
    private readonly HashSet<int> hiddenColumns;

    public TableColumnLayout(int columnCount)
        : this(columnCount, Enumerable.Range(0, columnCount), [])
    {
    }

    private TableColumnLayout(
        int columnCount,
        IEnumerable<int> order,
        IEnumerable<int> hiddenColumns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        this.columnCount = columnCount;
        this.order = [.. order];
        this.hiddenColumns = [.. hiddenColumns];
        ValidateOrder(this.order);
    }

    public IReadOnlyList<int> Order => order;

    public IReadOnlyList<int> VisibleColumnIndexes =>
        order.Where(index => !hiddenColumns.Contains(index)).ToArray();

    public IReadOnlyList<int> HiddenColumnIndexes =>
        order.Where(hiddenColumns.Contains).ToArray();

    public int VisibleCount => columnCount - hiddenColumns.Count;

    public bool IsDefault =>
        hiddenColumns.Count == 0 && order.SequenceEqual(Enumerable.Range(0, columnCount));

    public bool IsHidden(int columnIndex)
    {
        ValidateColumnIndex(columnIndex);
        return hiddenColumns.Contains(columnIndex);
    }

    public bool Hide(int columnIndex)
    {
        ValidateColumnIndex(columnIndex);
        return VisibleCount > 1 && hiddenColumns.Add(columnIndex);
    }

    public bool Show(int columnIndex)
    {
        ValidateColumnIndex(columnIndex);
        return hiddenColumns.Remove(columnIndex);
    }

    public bool MoveLeft(int columnIndex) => Move(columnIndex, -1);

    public bool MoveRight(int columnIndex) => Move(columnIndex, 1);

    public void SetOrder(IEnumerable<int> columnIndexes)
    {
        var candidate = columnIndexes.ToArray();
        ValidateOrder(candidate);
        order.Clear();
        order.AddRange(candidate);
    }

    public void ShowAll() => hiddenColumns.Clear();

    public void Reset()
    {
        hiddenColumns.Clear();
        order.Clear();
        order.AddRange(Enumerable.Range(0, columnCount));
    }

    public TableColumnLayout Clone() => new(columnCount, order, hiddenColumns);

    private bool Move(int columnIndex, int direction)
    {
        ValidateColumnIndex(columnIndex);
        if (hiddenColumns.Contains(columnIndex)) return false;

        var visible = VisibleColumnIndexes;
        var visiblePosition = visible.ToList().IndexOf(columnIndex);
        var targetPosition = visiblePosition + direction;
        if (visiblePosition < 0 || targetPosition < 0 || targetPosition >= visible.Count)
            return false;

        var currentOrderPosition = order.IndexOf(columnIndex);
        var targetOrderPosition = order.IndexOf(visible[targetPosition]);
        (order[currentOrderPosition], order[targetOrderPosition]) =
            (order[targetOrderPosition], order[currentOrderPosition]);
        return true;
    }

    private void ValidateColumnIndex(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= columnCount)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));
    }

    private void ValidateOrder(IReadOnlyCollection<int> candidate)
    {
        if (candidate.Count != columnCount ||
            !candidate.Order().SequenceEqual(Enumerable.Range(0, columnCount)))
            throw new ArgumentException("Column order must contain every column exactly once.", nameof(candidate));
    }
}

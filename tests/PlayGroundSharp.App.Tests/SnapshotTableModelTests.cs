using PlayGroundSharp.Core;

namespace PlayGroundSharp.App.Tests;

public sealed class SnapshotTableModelTests
{
    [Fact]
    public void ObjectSequencesBecomeTablesWithUnionedColumns()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "Row[]",
            Items:
            [
                Row(("Id", Number("1")), ("Name", Text("Ada"))),
                Row(("Id", Number("2")), ("Active", Boolean("true")))
            ],
            TotalCount: 2);

        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(snapshot));

        Assert.True(table.PreferTableView);
        Assert.Equal(["Id", "Name", "Active"], table.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["1", "Ada", ""], table.Rows[0].Cells.Select(static cell => cell.Display));
        Assert.Equal(["2", "", "true"], table.Rows[1].Cells.Select(static cell => cell.Display));
    }

    [Fact]
    public void DelimitedExportIncludesHeadersAndEscapesValues()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "1 item",
            "Row[]",
            Items:
            [
                Row(
                    ("Id", Number("1")),
                    ("Name", Text("Ada, \"Countess\"")),
                    ("Note", Text("first\nsecond")))
            ],
            TotalCount: 1);
        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(snapshot));

        var csv = table.FormatDelimited(',');
        var tsv = table.FormatDelimited('\t');

        Assert.Equal(
            $"Id,Name,Note{Environment.NewLine}1,\"Ada, \"\"Countess\"\"\",\"first\nsecond\"",
            csv);
        Assert.Equal(
            $"Id\tName\tNote{Environment.NewLine}1\t\"Ada, \"\"Countess\"\"\"\t\"first\nsecond\"",
            tsv);
    }

    [Fact]
    public void ScalarSequencesOfferAnOptionalSingleValueColumn()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "System.Int32[]",
            Items: [Number("1"), Number("2")],
            TotalCount: 2);

        var table = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(snapshot));

        Assert.True(SnapshotTableModel.CanCreate(snapshot));
        Assert.False(table.PreferTableView);
        Assert.True(table.HasSyntheticValueColumn);
        Assert.Equal(["Value"], table.Columns);
        Assert.Equal(["1", "2"], table.Rows.Select(static row => row.Cells[0].Display));
    }

    [Fact]
    public void StructuredCellsRetainTheirSourceForNestedTableNavigation()
    {
        var orders = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "Order[]",
            Items:
            [
                Row(("Id", Number("101")), ("Total", Number("25"))),
                Row(("Id", Number("102")), ("Total", Number("40")))
            ],
            TotalCount: 2);
        var profile = Row(("Country", Text("UK")), ("Active", Boolean("true")));
        var scores = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "System.Int32[]",
            Items: [Number("8"), Number("13")],
            TotalCount: 2);
        var customers = new ResultSnapshot(
            SnapshotKind.Sequence,
            "1 item",
            "Customer[]",
            Items: [Row(("Name", Text("Ada")), ("Profile", profile), ("Orders", orders), ("Scores", scores))],
            TotalCount: 1);

        var customerTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var profileColumn = customerTable.Columns.ToList().IndexOf("Profile");
        var ordersColumn = customerTable.Columns.ToList().IndexOf("Orders");
        var scoresColumn = customerTable.Columns.ToList().IndexOf("Scores");
        var profileCell = customerTable.Rows[0].Cells[profileColumn];
        var ordersCell = customerTable.Rows[0].Cells[ordersColumn];
        var scoresCell = customerTable.Rows[0].Cells[scoresColumn];
        var profileTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(profileCell.Source!));
        var ordersTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(ordersCell.Source!));
        var scoresTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(scoresCell.Source!));

        Assert.Same(profile, profileCell.Source);
        Assert.Same(orders, ordersCell.Source);
        Assert.Same(scores, scoresCell.Source);
        Assert.True(SnapshotTableModel.CanCreate(profileCell.Source!));
        Assert.True(SnapshotTableModel.CanCreate(ordersCell.Source!));
        Assert.True(customerTable.SourceRowsAreItems);
        Assert.Equal(0, customerTable.Rows[0].SourceIndex);
        Assert.Equal(["Country", "Active"], profileTable.Columns);
        Assert.Single(profileTable.Rows);
        Assert.Equal(["Id", "Total"], ordersTable.Columns);
        Assert.Equal(2, ordersTable.Rows.Count);
        Assert.False(scoresTable.PreferTableView);
        Assert.True(scoresTable.HasSyntheticValueColumn);
        Assert.Equal(["Value"], scoresTable.Columns);
        Assert.Equal(["8", "13"], scoresTable.Rows.Select(static row => row.Cells[0].Display));
        Assert.False(scoresTable.TryGetCell(
            customerTable.Rows[0],
            scoresColumn,
            out var staleParentCell));
        Assert.Same(SnapshotTableCell.Missing, staleParentCell);
    }

    [Fact]
    public void ArrayValuedColumnsFlattenItemsAcrossAllRows()
    {
        var customers = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "Customer[]",
            Items:
            [
                Row(
                    ("Name", Text("Ada")),
                    ("Orders", Sequence(
                        Row(("Id", Number("101")), ("Total", Number("25"))),
                        Row(("Id", Number("102")), ("Total", Number("40")))))),
                Row(
                    ("Name", Text("Grace")),
                    ("Orders", Sequence(
                        Row(("Id", Number("201")), ("Total", Number("60"))))))
            ],
            TotalCount: 2);
        var customerTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var ordersColumn = customerTable.Columns.ToList().IndexOf("Orders");

        var ordersTable = Assert.IsType<SnapshotTableModel>(
            customerTable.TryCreateFlattenedColumn(ordersColumn));

        Assert.True(customerTable.CanFlattenColumn(ordersColumn));
        Assert.Equal(["Id", "Total"], ordersTable.Columns);
        Assert.Equal(3, ordersTable.Rows.Count);
        Assert.Equal(["101", "102", "201"], ordersTable.Rows.Select(static row => row.Cells[0].Display));
        Assert.Equal(3, ordersTable.TotalRowCount);
        Assert.False(ordersTable.RowsTruncated);
    }

    [Fact]
    public void FlattenedColumnsPreserveCapturedTruncation()
    {
        var first = new ResultSnapshot(
            SnapshotKind.Sequence,
            "1 captured item",
            "Order[]",
            Items: [Row(("Id", Number("101")))],
            IsTruncated: true,
            TotalCount: 4);
        var second = Sequence(Row(("Id", Number("201"))), Row(("Id", Number("202"))));
        var customers = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "Customer[]",
            Items: [Row(("Orders", first)), Row(("Orders", second))],
            TotalCount: 2);
        var customerTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var ordersColumn = customerTable.Columns.ToList().IndexOf("Orders");

        var ordersTable = Assert.IsType<SnapshotTableModel>(
            customerTable.TryCreateFlattenedColumn(ordersColumn));

        Assert.Equal(3, ordersTable.Rows.Count);
        Assert.Equal(6, ordersTable.TotalRowCount);
        Assert.True(ordersTable.RowsTruncated);
    }

    [Fact]
    public void EmptyArrayCellCanOpenWhenAnotherRowContainsItems()
    {
        var customers = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "Customer[]",
            Items:
            [
                Row(("Orders", Sequence())),
                Row(("Orders", Sequence(Row(("Id", Number("201"))))))
            ],
            TotalCount: 2);
        var customerTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var ordersColumn = customerTable.Columns.ToList().IndexOf("Orders");

        Assert.True(customerTable.CanFlattenColumn(ordersColumn));
        var ordersTable = Assert.IsType<SnapshotTableModel>(
            customerTable.TryCreateFlattenedColumn(ordersColumn));
        Assert.Single(ordersTable.Rows);
        Assert.Equal("201", ordersTable.Rows[0].Cells[0].Display);
    }

    [Fact]
    public void FlattenedColumnHasUnknownTotalWhenParentRowsWereNotCaptured()
    {
        var customers = new ResultSnapshot(
            SnapshotKind.Sequence,
            "1 captured item",
            "Customer[]",
            Items: [Row(("Orders", Sequence(Row(("Id", Number("101"))))))],
            IsTruncated: true,
            TotalCount: 2);
        var customerTable = Assert.IsType<SnapshotTableModel>(SnapshotTableModel.TryCreate(customers));
        var ordersColumn = customerTable.Columns.ToList().IndexOf("Orders");

        var ordersTable = Assert.IsType<SnapshotTableModel>(
            customerTable.TryCreateFlattenedColumn(ordersColumn));

        Assert.Null(ordersTable.TotalRowCount);
        Assert.True(ordersTable.RowsTruncated);
    }

    private static ResultSnapshot Row(params (string Name, ResultSnapshot Value)[] properties) => new(
        SnapshotKind.Object,
        $"{properties.Length} properties",
        "Row",
        Properties: properties.Select(static property => new ResultProperty(property.Name, property.Value)).ToArray());

    private static ResultSnapshot Number(string value) =>
        new(SnapshotKind.Number, value, "System.Int32");

    private static ResultSnapshot Text(string value) =>
        new(SnapshotKind.String, value, "System.String");

    private static ResultSnapshot Boolean(string value) =>
        new(SnapshotKind.Boolean, value, "System.Boolean");

    private static ResultSnapshot Sequence(params ResultSnapshot[] items) =>
        new(SnapshotKind.Sequence, $"{items.Length} items", "System.Object[]", Items: items, TotalCount: items.Length);
}

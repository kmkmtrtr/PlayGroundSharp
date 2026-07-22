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
    public void ScalarSequencesDoNotOfferAMisleadingTableView()
    {
        var snapshot = new ResultSnapshot(
            SnapshotKind.Sequence,
            "2 items",
            "System.Int32[]",
            Items: [Number("1"), Number("2")],
            TotalCount: 2);

        Assert.Null(SnapshotTableModel.TryCreate(snapshot));
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
}

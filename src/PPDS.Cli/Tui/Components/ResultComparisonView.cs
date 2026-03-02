using System.Data;
using System.Text;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Components;

/// <summary>
/// Compares result sets from two query tabs and shows a summary and diff.
/// Shows: "N in A only | M in B only | P in both"
/// </summary>
internal sealed class ResultComparisonView : Dialog
{
    private readonly Label _summaryLabel;
    private readonly ListView _leftList;
    private readonly ListView _rightList;
    private ComparisonResult? _comparison;

    public ResultComparisonView()
        : base("Result Comparison", 80, 25)
    {
        ColorScheme = Infrastructure.TuiColorPalette.Default;

        _summaryLabel = new Label("")
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill() - 2,
            Height = 1
        };

        var leftFrame = new FrameView("In A Only")
        {
            X = 1,
            Y = 2,
            Width = Dim.Percent(50) - 1,
            Height = Dim.Fill() - 3
        };
        _leftList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = Infrastructure.TuiColorPalette.Default
        };
        leftFrame.Add(_leftList);

        var rightFrame = new FrameView("In B Only")
        {
            X = Pos.Right(leftFrame) + 1,
            Y = 2,
            Width = Dim.Fill() - 1,
            Height = Dim.Fill() - 3
        };
        _rightList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = Infrastructure.TuiColorPalette.Default
        };
        rightFrame.Add(_rightList);

        var closeButton = new Button("Close")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(_summaryLabel, leftFrame, rightFrame, closeButton);
        closeButton.SetFocus();
    }

    /// <summary>
    /// Sets the comparison data from two DataTables.
    /// </summary>
    /// <param name="tableA">The first result set (Tab A).</param>
    /// <param name="tableB">The second result set (Tab B).</param>
    /// <param name="labelA">Display label for set A.</param>
    /// <param name="labelB">Display label for set B.</param>
    public void SetData(DataTable tableA, DataTable tableB, string labelA = "A", string labelB = "B")
    {
        _comparison = CompareResults(tableA, tableB);
        _summaryLabel.Text = $"{_comparison.InAOnly.Count} in {labelA} only | " +
                             $"{_comparison.InBOnly.Count} in {labelB} only | " +
                             $"{_comparison.InBoth} in both";

        _leftList.SetSource(_comparison.InAOnly);
        _rightList.SetSource(_comparison.InBOnly);
    }

    /// <summary>
    /// Compares two DataTables and returns the set differences.
    /// This is a static method so it can be tested independently of Terminal.Gui.
    /// </summary>
    internal static ComparisonResult CompareResults(DataTable tableA, DataTable tableB)
    {
        var setA = DataTableToRowSet(tableA);
        var setB = DataTableToRowSet(tableB);

        var inAOnly = new List<string>();
        var inBOnly = new List<string>();
        var inBoth = 0;

        foreach (var row in setA)
        {
            if (setB.Contains(row))
                inBoth++;
            else
                inAOnly.Add(row);
        }

        foreach (var row in setB)
        {
            if (!setA.Contains(row))
                inBOnly.Add(row);
        }

        return new ComparisonResult(inAOnly, inBOnly, inBoth);
    }

    /// <summary>
    /// Converts a DataTable to a HashSet of row strings for comparison.
    /// Each row is serialized as a pipe-delimited string of its column values.
    /// </summary>
    internal static HashSet<string> DataTableToRowSet(DataTable table)
    {
        var set = new HashSet<string>();
        foreach (DataRow row in table.Rows)
        {
            set.Add(RowToString(row));
        }
        return set;
    }

    /// <summary>
    /// Serializes a DataRow to a pipe-delimited string of its values.
    /// </summary>
    internal static string RowToString(DataRow row)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < row.Table.Columns.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            var val = row[i];
            sb.Append(val == DBNull.Value ? "(null)" : val?.ToString() ?? "(null)");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Result of comparing two result sets.
    /// </summary>
    internal sealed record ComparisonResult(
        List<string> InAOnly,
        List<string> InBOnly,
        int InBoth);
}

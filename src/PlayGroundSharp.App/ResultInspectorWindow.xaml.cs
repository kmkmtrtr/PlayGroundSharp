using System.Windows;
using System.Windows.Controls;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public partial class ResultInspectorWindow : Window
{
    public ResultInspectorWindow(ResultSnapshot snapshot)
    {
        Roots = [SnapshotTreeNode.CreateRoot(snapshot)];
        InitializeComponent();
        DataContext = this;
        DetailText.Text = Roots[0].Detail;
    }

    public IReadOnlyList<SnapshotTreeNode> Roots { get; }

    private void SnapshotTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SnapshotTreeNode node) DetailText.Text = node.Detail;
    }
}

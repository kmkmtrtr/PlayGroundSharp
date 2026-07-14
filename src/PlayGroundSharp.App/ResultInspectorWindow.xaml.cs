using System.Windows;
using System.Windows.Controls;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public partial class ResultInspectorWindow : Window
{
    private SnapshotTreeNode selectedNode;

    public ResultInspectorWindow(ResultSnapshot snapshot)
    {
        Roots = [SnapshotTreeNode.CreateRoot(snapshot)];
        selectedNode = Roots[0];
        InitializeComponent();
        DataContext = this;
        DetailText.Text = Roots[0].Detail;
    }

    public IReadOnlyList<SnapshotTreeNode> Roots { get; }

    private void SnapshotTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not SnapshotTreeNode node) return;
        selectedNode = node;
        DetailText.Text = node.Detail;
    }

    private void ExpandSelected_Click(object sender, RoutedEventArgs e) => selectedNode.IsExpanded = true;

    private void CollapseSelected_Click(object sender, RoutedEventArgs e) => selectedNode.IsExpanded = false;

    private async void CopySelected_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(await Task.Run(() => selectedNode.CopyText));

    private async void CopyAll_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(await Task.Run(() => Roots[0].CopyText));

    private void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using PlayGroundSharp.Core;

namespace PlayGroundSharp.App;

public partial class ResultInspectorWindow : Window
{
    private readonly AppLanguageMode languageMode;
    private readonly ResultSnapshot snapshot;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private SnapshotTreeNode? selectedNode;

    public ResultInspectorWindow(ResultSnapshot snapshot, AppLanguageMode languageMode)
    {
        this.snapshot = snapshot;
        this.languageMode = languageMode;
        Roots = [SnapshotTreeNode.CreateRoot(snapshot, languageMode)];
        selectedNode = Roots[0];
        InitializeComponent();
        DataContext = this;
        SetSelectedNode(Roots[0]);
        searchTimer.Tick += (_, _) => ApplySearch();
        Closed += (_, _) => searchTimer.Stop();
    }

    public ObservableCollection<SnapshotTreeNode> Roots { get; }

    private void SnapshotTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not SnapshotTreeNode node) return;
        SetSelectedNode(node);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void ApplySearch()
    {
        searchTimer.Stop();
        var root = SnapshotTreeNode.CreateFilteredRoot(snapshot, languageMode, SearchBox.Text, out var matches);
        Roots.Clear();
        if (root is not null)
        {
            Roots.Add(root);
            SetSelectedNode(root);
        }
        else
        {
            selectedNode = null;
            DetailText.Clear();
            PathText.Text = string.Empty;
        }
        SearchStatus.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? string.Empty
            : root is null
                ? AppLocalization.Text(languageMode, "Inspector.NoMatches")
                : AppLocalization.Text(languageMode, "Inspector.MatchCount", matches);
    }

    private void ExpandSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not null) selectedNode.IsExpanded = true;
    }

    private void CollapseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not null) selectedNode.IsExpanded = false;
    }

    private async void CopySelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is null) return;
        CopyToClipboard(await Task.Run(() => selectedNode.CopyText));
    }

    private async void CopyAll_Click(object sender, RoutedEventArgs e) =>
        CopyToClipboard(await Task.Run(() => SnapshotTextFormatter.FormatFull(snapshot)));

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (selectedNode is not null) CopyToClipboard(selectedNode.Path);
    }

    private async void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = AppLocalization.Text(languageMode, "Dialog.ResultSaveTitle"),
            Filter = AppLocalization.Text(languageMode, "Dialog.ResultFileFilter"),
            DefaultExt = ".txt",
            AddExtension = true,
            FileName = $"PlayGroundSharp-result-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var text = await Task.Run(() => SnapshotTextFormatter.FormatFull(snapshot));
            await File.WriteAllTextAsync(dialog.FileName, text);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SetSelectedNode(SnapshotTreeNode node)
    {
        selectedNode = node;
        DetailText.Text = node.Detail;
        PathText.Text = node.Path;
    }

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

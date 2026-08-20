using System.Windows.Input;

namespace PlayGroundSharp.App.Tests;

public sealed class ExecutionShortcutTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void ControlCCancelsOnlyRunningExecutionWithoutACopyTarget(
        bool isRunning,
        bool hasCopyTarget,
        bool expected)
    {
        Assert.Equal(expected, MainWindow.ShouldCancelExecutionWithControlC(
            Key.C,
            ModifierKeys.Control,
            isRunning,
            hasCopyTarget));
    }

    [Theory]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.X, ModifierKeys.Control)]
    [InlineData(Key.C, ModifierKeys.None)]
    public void OtherShortcutsDoNotCancel(Key key, ModifierKeys modifiers)
    {
        Assert.False(MainWindow.ShouldCancelExecutionWithControlC(
            key,
            modifiers,
            isRunning: true,
            hasFocusedCopyTarget: false));
    }
}

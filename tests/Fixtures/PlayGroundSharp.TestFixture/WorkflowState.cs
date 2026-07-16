namespace PlayGroundSharp.TestFixture;

/// <summary>Describes the current workflow state.</summary>
public enum WorkflowState
{
    /// <summary>The workflow has not started.</summary>
    Pending = 10,

    /// <summary>The workflow is currently running.</summary>
    Running = 20,

    /// <summary>The workflow completed successfully.</summary>
    Completed = 30
}

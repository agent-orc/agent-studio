namespace AgentStudio.Pipeline;

/// <summary>
/// Read-model hook behind <see cref="PipelineExecutionLog.RecordStep"/>. It is
/// called after the step row is durable, outside the per-folder lock, with the
/// measured row (cost stamped, run count carried) and the record it landed in.
/// An observer must not write the pipeline log back; a failure is logged and
/// never undoes the step write.
/// </summary>
public interface IPipelineStepObserver
{
    void StepRecorded(string jobFolderPath, PipelineExecutionRecord record, PipelineStepExecution step);
}

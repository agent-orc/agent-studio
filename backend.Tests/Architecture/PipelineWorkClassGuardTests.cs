using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests.Architecture;

[Trait("Category", "Guard")]
public sealed class PipelineWorkClassGuardTests
{
    [Fact]
    public void Every_catalogue_step_declares_its_work_class()
    {
        var steps = PipelineCatalogue.All.SelectMany(pipeline => pipeline.AllSteps)
            .Append(PipelineCatalogue.AbortReviewStep).Append(PipelineCatalogue.RemoteConceptFitStep);
        Assert.All(steps, step => Assert.False(string.IsNullOrWhiteSpace(step.WorkClass),
            $"Pipeline step {step.Id} needs an explicit work class before it can enter the catalogue."));
    }
}

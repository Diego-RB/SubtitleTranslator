using SubtitleTranslator.Domain.Entities;

namespace SubtitleTranslator.Application.Interfaces;

public interface IPipelineDefinitionService
{
    Task<Pipeline> GetRequiredAsync(int pipelineId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PipelineStep>> GetEnabledStepsAsync(
        int pipelineId,
        CancellationToken cancellationToken = default);
}

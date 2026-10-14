#nullable enable
using Rug.UI.Core.Models;
using Rug.UI.Core.Services;

namespace Rug.UI.Core.Abstractions;

public interface ICheckpointCoordinator
{
    Task<CheckpointResult> CommitAsync(TaskExecutionContext context, string nodeId, string stateJson, string completedActionIdsJson, CancellationToken token = default);
}
public interface ICheckpointValidator
{
    Task<bool> ValidateAsync(CheckpointObservation observation, CheckpointSubmission submission, CancellationToken token);
}
public interface ICheckpointNodeRegistry
{
    (CheckpointNode Node, ICheckpointValidator Validator)? Find(CheckpointRunMetadata metadata, string nodeId);
}
public interface ICheckpointStore : IAsyncDisposable
{
    Task<CheckpointResult> PublishAsync(CheckpointRecord record, Func<bool> beginPublish, CancellationToken token);
    Task<CheckpointReadResult> ReadAsync(CheckpointAddress address, CheckpointCompatibility compatibility, CancellationToken token = default);
}
/// <summary>Trusted replaceable IO boundary; publication has already won cancellation before this call.</summary>
public interface ICheckpointFilePublisher
{
    Task PublishAsync(string temporaryPath, string destinationPath, bool replace);
}

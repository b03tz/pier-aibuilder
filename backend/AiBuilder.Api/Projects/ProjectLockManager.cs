using System.Collections.Concurrent;

namespace AiBuilder.Api.Projects;

// Shared per-project mutex. Any work that mutates a project's on-disk
// workspace or its git state must acquire the lock before running so we
// don't have two subprocesses stepping on each other. Used by both the
// build orchestrator and the VCS push orchestrator.
public sealed class ProjectLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SemaphoreSlim Get(string projectId) =>
        _locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));

    // Try-acquire with zero timeout — used by `StartAsync` paths where
    // "already running" must be an immediate 409, not a wait.
    public Task<bool> TryAcquireAsync(string projectId, CancellationToken ct) =>
        Get(projectId).WaitAsync(TimeSpan.Zero, ct);
}

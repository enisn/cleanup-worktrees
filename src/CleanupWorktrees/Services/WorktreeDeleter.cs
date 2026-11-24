using CleanupWorktrees.Models;

namespace CleanupWorktrees.Services;

public static class WorktreeDeleter
{
    public static async Task<CleanupReport> DeleteAsync(
        string repoRoot,
        IReadOnlyList<WorktreeInfo> targets,
        Action<int, int, WorktreeInfo>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RemovalResult>(targets.Count);
        var pendingPruneIndices = new List<int>();
        var needsPrune = false;

        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = targets[i];
            onProgress?.Invoke(i + 1, targets.Count, target);

            try
            {
                if (!target.Exists)
                {
                    if (target.IsLocked)
                    {
                        var unlockResult = await GitRunner.RunAsync(
                            repoRoot,
                            ["worktree", "unlock", target.Path],
                            cancellationToken);

                        if (!unlockResult.Success)
                        {
                            results.Add(new RemovalResult(
                                target,
                                RemovalOutcome.Failed,
                                0,
                                $"Failed to unlock missing worktree before prune: {unlockResult.CombinedError}"));
                            continue;
                        }
                    }

                    pendingPruneIndices.Add(results.Count);
                    results.Add(new RemovalResult(target, RemovalOutcome.Pruned, 0, null));
                    needsPrune = true;
                    continue;
                }

                var args = target.IsLocked
                    ? new[] { "worktree", "remove", "--force", "--force", target.Path }
                    : new[] { "worktree", "remove", "--force", target.Path };

                var removeResult = await GitRunner.RunAsync(repoRoot, args, cancellationToken);
                if (removeResult.Success)
                {
                    results.Add(new RemovalResult(
                        target,
                        RemovalOutcome.Removed,
                        target.SizeBytes,
                        null));
                }
                else
                {
                    needsPrune = true;
                    var dirStillExists = Directory.Exists(target.Path);
                    if (!dirStillExists)
                    {
                        results.Add(new RemovalResult(
                            target,
                            RemovalOutcome.PartiallyRemoved,
                            target.SizeBytes,
                            $"Directory deleted from disk, but git metadata cleanup failed: {removeResult.CombinedError}"));
                    }
                    else
                    {
                        results.Add(new RemovalResult(
                            target,
                            RemovalOutcome.Failed,
                            0,
                            removeResult.CombinedError));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new RemovalResult(
                    target,
                    RemovalOutcome.Failed,
                    0,
                    ex.Message));
            }
        }

        var pruneAttempted = false;
        var pruneSucceeded = true;
        string? pruneError = null;

        if (needsPrune)
        {
            pruneAttempted = true;
            var pruneResult = await GitRunner.RunAsync(repoRoot, ["worktree", "prune"], cancellationToken);
            pruneSucceeded = pruneResult.Success;
            if (!pruneResult.Success)
            {
                pruneError = pruneResult.CombinedError;

                // Any missing worktrees that relied on prune failed to prune cleanly.
                foreach (var idx in pendingPruneIndices)
                {
                    var item = results[idx];
                    results[idx] = item with
                    {
                        Outcome = RemovalOutcome.Failed,
                        ErrorMessage = $"git worktree prune failed: {pruneError}"
                    };
                }
            }
        }

        return new CleanupReport(results, pruneAttempted, pruneSucceeded, pruneError);
    }
}

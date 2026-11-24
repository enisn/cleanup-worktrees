using System.Globalization;
using System.IO.Enumeration;
using CleanupWorktrees.Models;

namespace CleanupWorktrees.Services;

public sealed record ScanOptions(
    double Days,
    bool CleanOnly,
    bool All,
    bool MissingOnly,
    bool SkipSize,
    IReadOnlyList<string> Excludes);

public sealed record ScanResult(
    string RepoRoot,
    string? ActiveWorktreeRoot,
    IReadOnlyList<WorktreeInfo> Worktrees);

public sealed record RawWorktreeEntry(
    int Index,
    string Path,
    string HeadSha,
    string Branch,
    bool IsBare,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    string? PrunableReason);

public static class WorktreeScanner
{
    public static async Task<(bool Ok, string? RepoRoot, string? ActiveWorktreeRoot, string? Error)> ResolveRepositoryAsync(
        string workDir,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(workDir);
        if (!Directory.Exists(fullPath))
        {
            return (false, null, null, $"Directory does not exist: {fullPath}");
        }

        var gitDirResult = await GitRunner.RunAsync(fullPath, ["rev-parse", "--git-dir"], cancellationToken);
        if (!gitDirResult.Success)
        {
            return (false, null, null, $"Not a git repository: {fullPath}\n{gitDirResult.CombinedError}");
        }

        var topLevelResult = await GitRunner.RunAsync(fullPath, ["rev-parse", "--show-toplevel"], cancellationToken);
        string? activeRoot = topLevelResult.Success && !string.IsNullOrWhiteSpace(topLevelResult.StdOut)
            ? CanonicalizePath(topLevelResult.StdOut.Trim())
            : null;

        return (true, CanonicalizePath(fullPath), activeRoot, null);
    }

    public static async Task<ScanResult> ScanAsync(
        string workDir,
        ScanOptions options,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveRepositoryAsync(workDir, cancellationToken);
        if (!resolved.Ok || resolved.RepoRoot is null)
        {
            throw new InvalidOperationException(resolved.Error ?? "Failed to resolve git repository.");
        }

        var listResult = await GitRunner.RunAsync(resolved.RepoRoot, ["worktree", "list", "--porcelain"], cancellationToken);
        if (!listResult.Success)
        {
            throw new InvalidOperationException($"Failed to list git worktrees: {listResult.CombinedError}");
        }

        var rawEntries = ParsePorcelain(listResult.StdOut);
        var mainRepoRoot = rawEntries.Count > 0 ? CanonicalizePath(rawEntries[0].Path) : resolved.RepoRoot;
        var activeRoot = resolved.ActiveWorktreeRoot;
        var now = DateTimeOffset.UtcNow;

        var allWorktreePaths = rawEntries
            .Where(e => Directory.Exists(e.Path))
            .Select(e => CanonicalizePath(e.Path))
            .ToHashSet(StringComparer.Ordinal);

        var results = new WorktreeInfo[rawEntries.Count];
        var completed = 0;

        await Parallel.ForEachAsync(
            rawEntries,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16),
                CancellationToken = cancellationToken
            },
            async (raw, ct) =>
            {
                var info = await InspectEntryAsync(raw, activeRoot, allWorktreePaths, options, now, ct);
                results[raw.Index - 1] = info;
                var done = Interlocked.Increment(ref completed);
                onProgress?.Invoke(done, rawEntries.Count, raw.Branch);
            });

        return new ScanResult(mainRepoRoot, activeRoot, results);
    }

    public static IReadOnlyList<RawWorktreeEntry> ParsePorcelain(string porcelainOutput)
    {
        var normalized = porcelainOutput.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return [];
        }

        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = new List<RawWorktreeEntry>(blocks.Length);

        for (var i = 0; i < blocks.Length; i++)
        {
            var path = "";
            var head = "";
            var branch = "";
            var isBare = false;
            var isLocked = false;
            string? lockReason = null;
            var isPrunable = false;
            string? prunableReason = null;

            foreach (var rawLine in blocks[i].Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    path = line["worktree ".Length..];
                }
                else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                {
                    head = line["HEAD ".Length..];
                }
                else if (line.StartsWith("branch ", StringComparison.Ordinal))
                {
                    var refName = line["branch ".Length..];
                    branch = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? refName["refs/heads/".Length..]
                        : refName;
                }
                else if (line == "detached")
                {
                    branch = "(detached)";
                }
                else if (line == "bare")
                {
                    isBare = true;
                    branch = "(bare)";
                }
                else if (line == "locked")
                {
                    isLocked = true;
                }
                else if (line.StartsWith("locked ", StringComparison.Ordinal))
                {
                    isLocked = true;
                    lockReason = line["locked ".Length..].Trim();
                }
                else if (line == "prunable")
                {
                    isPrunable = true;
                }
                else if (line.StartsWith("prunable ", StringComparison.Ordinal))
                {
                    isPrunable = true;
                    prunableReason = line["prunable ".Length..].Trim();
                }
            }

            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (string.IsNullOrEmpty(branch))
            {
                branch = "(unknown)";
            }

            entries.Add(new RawWorktreeEntry(
                Index: i + 1,
                Path: path,
                HeadSha: head,
                Branch: branch,
                IsBare: isBare,
                IsLocked: isLocked,
                LockReason: lockReason,
                IsPrunable: isPrunable,
                PrunableReason: prunableReason));
        }

        return entries;
    }

    private static async Task<WorktreeInfo> InspectEntryAsync(
        RawWorktreeEntry raw,
        string? activeWorktreeRoot,
        HashSet<string> allWorktreePaths,
        ScanOptions options,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var exists = Directory.Exists(raw.Path);
        var isMain = raw.Index == 1;
        var canonicalPath = exists ? CanonicalizePath(raw.Path) : Path.GetFullPath(raw.Path);
        var isCurrent = exists
            && activeWorktreeRoot is not null
            && string.Equals(canonicalPath, activeWorktreeRoot, StringComparison.Ordinal);

        DateTimeOffset? lastCommitAt = null;
        DateTimeOffset? lastModifiedAt = null;
        long sizeBytes = 0;
        var sizeCalculated = !exists && !options.SkipSize;
        var status = exists ? WorktreeGitStatus.Clean : WorktreeGitStatus.Missing;
        string? statusError = null;

        if (exists && !raw.IsBare)
        {
            var commitTask = GetLastCommitDateAsync(raw.Path, cancellationToken);
            var statusTask = GetGitStatusAsync(raw.Path, cancellationToken);

            // Walk directory for last mtime & size in a single fast pass.
            // Skip scanning main/bare repo if --no-size is set since main is always protected.
            var shouldWalk = !(options.SkipSize && (isMain || isCurrent || options.All || options.MissingOnly));
            (DateTimeOffset? mtime, long bytes, bool hasSize) metrics = (null, 0, false);

            if (shouldWalk)
            {
                metrics = await Task.Run(
                    () => ScanDirectoryMetrics(canonicalPath, allWorktreePaths, calculateSize: !options.SkipSize, cancellationToken),
                    cancellationToken);
            }

            lastCommitAt = await commitTask;
            (status, statusError) = await statusTask;
            lastModifiedAt = metrics.mtime;
            sizeBytes = metrics.bytes;
            sizeCalculated = metrics.hasSize;
        }
        else if (exists && raw.IsBare)
        {
            status = WorktreeGitStatus.Clean;
            if (!options.SkipSize)
            {
                var metrics = await Task.Run(
                    () => ScanDirectoryMetrics(canonicalPath, allWorktreePaths, calculateSize: true, cancellationToken),
                    cancellationToken);
                lastModifiedAt = metrics.mtime;
                sizeBytes = metrics.bytes;
                sizeCalculated = metrics.hasSize;
            }
        }

        var draft = new WorktreeInfo
        {
            Index = raw.Index,
            Path = raw.Path,
            Branch = raw.Branch,
            HeadSha = raw.HeadSha,
            Exists = exists,
            IsMain = isMain,
            IsCurrent = isCurrent,
            IsBare = raw.IsBare,
            IsLocked = raw.IsLocked,
            LockReason = raw.LockReason,
            IsPrunable = raw.IsPrunable,
            PrunableReason = raw.PrunableReason,
            Status = status,
            StatusError = statusError,
            LastCommitAt = lastCommitAt,
            LastModifiedAt = lastModifiedAt,
            SizeBytes = sizeBytes,
            SizeCalculated = sizeCalculated
        };

        return EvaluateSelection(draft, options, now);
    }

    public static WorktreeInfo EvaluateSelection(
        WorktreeInfo candidate,
        ScanOptions options,
        DateTimeOffset now)
    {
        var isExcluded = options.Excludes.Any(pattern => FormatHelpers.MatchesExclude(pattern, candidate));
        var daysLabel = FormatDays(options.Days);

        bool preSelected;
        string reason;

        if (candidate.IsMain)
        {
            preSelected = false;
            reason = "Main repo";
        }
        else if (candidate.IsBare)
        {
            preSelected = false;
            reason = "Bare repo";
        }
        else if (candidate.IsCurrent)
        {
            preSelected = false;
            reason = "Active worktree";
        }
        else if (isExcluded)
        {
            preSelected = false;
            reason = "Excluded";
        }
        else if (!candidate.Exists || candidate.IsPrunable)
        {
            if (candidate.IsLocked)
            {
                preSelected = false;
                reason = "Missing (locked)";
            }
            else
            {
                preSelected = true;
                reason = "Missing (prune)";
            }
        }
        else if (options.MissingOnly)
        {
            preSelected = false;
            reason = "Exists (--missing-only)";
        }
        else if (candidate.IsLocked)
        {
            preSelected = false;
            reason = string.IsNullOrWhiteSpace(candidate.LockReason)
                ? "Locked"
                : $"Locked ({FormatHelpers.Truncate(candidate.LockReason, 24)})";
        }
        else if (options.CleanOnly && candidate.Status == WorktreeGitStatus.Dirty)
        {
            preSelected = false;
            reason = "Dirty (--clean-only)";
        }
        else if (options.CleanOnly && candidate.Status == WorktreeGitStatus.Error)
        {
            preSelected = false;
            reason = "Status error (--clean-only)";
        }
        else if (options.All)
        {
            preSelected = true;
            reason = "All (--all)";
        }
        else
        {
            var cutoff = now - TimeSpan.FromDays(options.Days);
            var commitOld = candidate.LastCommitAt is null || candidate.LastCommitAt.Value < cutoff;
            var filesOld = candidate.LastModifiedAt is null || candidate.LastModifiedAt.Value < cutoff;

            if (commitOld && filesOld)
            {
                preSelected = true;
                reason = $">{daysLabel} old";
            }
            else
            {
                preSelected = false;
                reason = $"<{daysLabel} recent";
            }
        }

        return candidate with
        {
            IsExcluded = isExcluded,
            IsPreSelected = preSelected,
            Reason = reason
        };
    }

    private static async Task<DateTimeOffset?> GetLastCommitDateAsync(string path, CancellationToken ct)
    {
        var result = await GitRunner.RunAsync(path, ["log", "-1", "--format=%ct"], ct);
        if (!result.Success)
        {
            return null;
        }

        var text = result.StdOut.Trim();
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }

    private static async Task<(WorktreeGitStatus Status, string? Error)> GetGitStatusAsync(string path, CancellationToken ct)
    {
        var result = await GitRunner.RunAsync(path, ["status", "--porcelain"], ct);
        if (!result.Success)
        {
            return (WorktreeGitStatus.Error, result.CombinedError);
        }

        return string.IsNullOrWhiteSpace(result.StdOut)
            ? (WorktreeGitStatus.Clean, null)
            : (WorktreeGitStatus.Dirty, null);
    }

    private static (DateTimeOffset? mtime, long bytes, bool hasSize) ScanDirectoryMetrics(
        string rootPath,
        HashSet<string> allWorktreePaths,
        bool calculateSize,
        CancellationToken ct)
    {
        long totalBytes = 0;
        long latestTicks = 0;
        var visited = 0;

        // Avoid double-counting nested worktrees (e.g., .claude/worktrees/* living inside main repo).
        var nestedWorktrees = allWorktreePaths
            .Where(p => !string.Equals(p, rootPath, StringComparison.Ordinal)
                && p.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        try
        {
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                ReturnSpecialDirectories = false
            };

            var enumerable = new FileSystemEnumerable<byte>(
                rootPath,
                (ref FileSystemEntry entry) =>
                {
                    if ((++visited & 0x3FF) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }

                    if (!entry.IsDirectory)
                    {
                        if (calculateSize)
                        {
                            totalBytes += entry.Length;
                        }

                        if (!entry.FileName.Equals(".git", StringComparison.Ordinal))
                        {
                            var writeTicks = entry.LastWriteTimeUtc.Ticks;
                            if (writeTicks > latestTicks)
                            {
                                latestTicks = writeTicks;
                            }
                        }
                    }

                    return 0;
                },
                enumOptions)
            {
                ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                {
                    if (entry.FileName.Equals(".git", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (nestedWorktrees.Count > 0)
                    {
                        var fullDir = Path.Join(entry.Directory, entry.FileName);
                        if (nestedWorktrees.Contains(fullDir))
                        {
                            return false;
                        }
                    }

                    return true;
                }
            };

            foreach (var _ in enumerable)
            {
                // Work is performed inline in the transform callback to avoid allocations.
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Keep partial metrics if an unexpected filesystem error occurs.
        }

        DateTimeOffset? lastModified = latestTicks > 0
            ? new DateTimeOffset(latestTicks, TimeSpan.Zero)
            : null;

        return (lastModified, totalBytes, calculateSize);
    }

    private static string CanonicalizePath(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            var info = new DirectoryInfo(full);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                return target.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            // Fall back to full path when link resolution fails.
        }

        return full;
    }

    private static string FormatDays(double days)
    {
        var formatted = days.ToString("0.##", CultureInfo.InvariantCulture);
        return days == 1.0 ? $"{formatted}d" : $"{formatted}d";
    }
}

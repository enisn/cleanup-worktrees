# cleanup-worktrees

A fast, interactive .NET TUI CLI built with `Spectre.Console` to inspect and clean up old Git worktrees.

## Install / Update as a Global .NET Tool

```bash
./install.sh
```

Or manually:

```bash
dotnet pack src/CleanupWorktrees/CleanupWorktrees.csproj -c Release
dotnet tool install --global --add-source src/CleanupWorktrees/nupkg CleanupWorktrees
# Or to update an existing installation:
dotnet tool update --global --add-source src/CleanupWorktrees/nupkg CleanupWorktrees
```

## Usage

```bash
# Run in the current directory (default: pre-select worktrees inactive > 1 day)
cleanup-worktrees

# Run against a specific repository path
cleanup-worktrees /path/to/repo

# Pre-select worktrees inactive > 7 days and skip DIRTY worktrees in pre-selection
cleanup-worktrees --days 7 --clean-only

# Exclude specific branches, folder names, or wildcard patterns
cleanup-worktrees -d 3 -e "release-*" -e my-important-wt

# Dry-run only (prints the table and exits)
cleanup-worktrees --dry-run

# Prune only worktrees missing on disk
cleanup-worktrees --missing-only

# Skip size calculation for faster scanning on massive repos
cleanup-worktrees --no-size

# Non-interactive deletion of pre-selected worktrees
cleanup-worktrees --days 7 --clean-only --yes
```

## Protections & Behavior

- **Main repository**, **bare repository**, and the **currently active worktree** (even when invoked from a nested subdirectory) are always protected and never selectable for deletion.
- A worktree is considered inactive when **both** its last commit date and its last file modification time (excluding `.git`) are older than `--days`.
- Missing worktrees on disk are marked for pruning (`git worktree prune`).
- Locked worktrees are never pre-selected by default, though you can still toggle them manually in the TUI.
- Errors during deletion do not abort the run: remaining worktrees are still processed, and all failures are displayed together in a bulk error report at the end.

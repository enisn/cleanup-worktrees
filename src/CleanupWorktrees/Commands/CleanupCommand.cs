using System.Globalization;
using CleanupWorktrees.Models;
using CleanupWorktrees.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CleanupWorktrees.Commands;

public sealed class CleanupCommand : AsyncCommand<CleanupSettings>
{
    public override async Task<int> ExecuteAsync(
        CommandContext context,
        CleanupSettings settings,
        CancellationToken cancellationToken)
    {
        var targetDir = string.IsNullOrWhiteSpace(settings.WorkDir)
            ? Directory.GetCurrentDirectory()
            : settings.WorkDir;

        var resolved = await WorktreeScanner.ResolveRepositoryAsync(targetDir, cancellationToken);
        if (!resolved.Ok || resolved.RepoRoot is null)
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {Markup.Escape(resolved.Error ?? "Invalid git directory.")}");
            return 1;
        }

        var scanOptions = new ScanOptions(
            Days: settings.Days,
            CleanOnly: settings.CleanOnly,
            All: settings.All,
            MissingOnly: settings.MissingOnly,
            SkipSize: settings.NoSize,
            Excludes: settings.Excludes);

        ScanResult scanResult;
        try
        {
            scanResult = await ScanWithProgressAsync(resolved.RepoRoot, scanOptions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[grey]Cancelled.[/]");
            return 130;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Scan failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        RenderHeader(scanResult.RepoRoot, settings);
        RenderSummaryTable(scanResult, settings);

        if (settings.DryRun)
        {
            return 0;
        }

        var removable = scanResult.Worktrees.Where(w => w.IsRemovable).ToList();
        if (removable.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No removable worktrees found.[/]");
            return 0;
        }

        IReadOnlyList<WorktreeInfo> selected;
        if (settings.Yes)
        {
            selected = removable.Where(w => w.IsPreSelected).ToList();
            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No worktrees matched the pre-selection criteria. Nothing to delete.[/]");
                return 0;
            }
        }
        else
        {
            if (!AnsiConsole.Profile.Capabilities.Interactive)
            {
                AnsiConsole.MarkupLine(
                    "[bold red]Error:[/] Terminal is not interactive. Use [cyan]--dry-run[/] to inspect or [cyan]--yes[/] to delete pre-selected worktrees non-interactively.");
                return 1;
            }

            selected = PromptForSelection(removable, scanResult.RepoRoot, settings.NoSize);
            if (selected.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]No worktrees selected. Nothing was deleted.[/]");
                return 0;
            }

            if (!ConfirmDeletion(selected, settings.NoSize))
            {
                AnsiConsole.MarkupLine("[grey]Cancelled. Nothing was deleted.[/]");
                return 0;
            }
        }

        CleanupReport report;
        try
        {
            report = await DeleteWithProgressAsync(scanResult.RepoRoot, selected, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Deletion interrupted by user.[/]");
            return 130;
        }

        RenderFinalReport(report, scanResult.RepoRoot, settings.NoSize);
        return report.HasFailures ? 1 : 0;
    }

    private static async Task<ScanResult> ScanWithProgressAsync(
        string repoRoot,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return await WorktreeScanner.ScanAsync(repoRoot, options, cancellationToken: cancellationToken);
        }

        ScanResult? result = null;
        await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(
                new SpinnerColumn(),
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[cyan]Scanning git worktrees...[/]");
                task.IsIndeterminate = true;

                result = await WorktreeScanner.ScanAsync(
                    repoRoot,
                    options,
                    onProgress: (done, total, branch) =>
                    {
                        task.IsIndeterminate = false;
                        task.MaxValue = Math.Max(total, 1);
                        task.Value = done;
                        task.Description = $"[cyan]Scanning worktrees[/] [grey]({done}/{total}: {Markup.Escape(FormatHelpers.Truncate(branch, 28))})[/]";
                    },
                    cancellationToken: cancellationToken);

                task.StopTask();
            });

        return result!;
    }

    private static void RenderHeader(string repoRoot, CleanupSettings settings)
    {
        var filters = new List<string>();
        if (settings.MissingOnly)
        {
            filters.Add("mode: [yellow]missing-only[/]");
        }
        else if (settings.All)
        {
            filters.Add("mode: [cyan]all[/]");
        }
        else
        {
            filters.Add($"inactive > [cyan]{settings.Days.ToString("0.##", CultureInfo.InvariantCulture)}d[/]");
        }

        if (settings.CleanOnly)
        {
            filters.Add("[green]clean-only[/]");
        }

        if (settings.Excludes.Length > 0)
        {
            filters.Add($"exclude: [grey]{Markup.Escape(string.Join(", ", settings.Excludes))}[/]");
        }

        if (settings.DryRun)
        {
            filters.Add("[yellow]dry-run[/]");
        }

        AnsiConsole.Write(new Rule($"[bold cyan]cleanup-worktrees[/] [grey]({Markup.Escape(FormatHelpers.ShortenPath(repoRoot, null))})[/]").LeftJustified());
        AnsiConsole.MarkupLine($"[grey]Filters:[/] {string.Join("  [grey]|[/]  ", filters)}");
        AnsiConsole.WriteLine();
    }

    private static void RenderSummaryTable(ScanResult scanResult, CleanupSettings settings)
    {
        var isWide = AnsiConsole.Profile.Width >= 120;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]#[/]").RightAligned().NoWrap())
            .AddColumn("[bold]Branch[/]")
            .AddColumn("[bold]Location[/]");

        if (isWide)
        {
            table
                .AddColumn(new TableColumn("[bold]Last Modified[/]").NoWrap())
                .AddColumn(new TableColumn("[bold]Last Commit[/]").NoWrap());
        }
        else
        {
            table.AddColumn("[bold]Mod / Commit[/]");
        }

        table
            .AddColumn("[bold]Status[/]")
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned().NoWrap())
            .AddColumn("[bold]Select[/]");

        foreach (var w in scanResult.Worktrees)
        {
            var shortPath = FormatHelpers.ShortenPath(w.Path, scanResult.RepoRoot);
            var branchMarkup = w.IsProtected
                ? $"[dim]{Markup.Escape(w.Branch)}[/]"
                : Markup.Escape(w.Branch);
            var pathMarkup = w.IsProtected
                ? $"[dim]{Markup.Escape(shortPath)}[/]"
                : Markup.Escape(shortPath);

            if (isWide)
            {
                table.AddRow(
                    w.Index.ToString(CultureInfo.InvariantCulture),
                    branchMarkup,
                    pathMarkup,
                    Markup.Escape(FormatHelpers.FormatDate(w.LastModifiedAt)),
                    Markup.Escape(FormatHelpers.FormatDate(w.LastCommitAt)),
                    FormatHelpers.FormatStatusMarkup(w),
                    Markup.Escape(FormatHelpers.FormatSize(w.SizeBytes, w.SizeCalculated)),
                    FormatHelpers.FormatPreSelectMarkup(w));
            }
            else
            {
                var datesMarkup =
                    $"M: {Markup.Escape(FormatHelpers.FormatDate(w.LastModifiedAt))}\n" +
                    $"[grey]C: {Markup.Escape(FormatHelpers.FormatDate(w.LastCommitAt))}[/]";
                table.AddRow(
                    w.Index.ToString(CultureInfo.InvariantCulture),
                    branchMarkup,
                    pathMarkup,
                    datesMarkup,
                    FormatHelpers.FormatStatusMarkup(w),
                    Markup.Escape(FormatHelpers.FormatSize(w.SizeBytes, w.SizeCalculated)),
                    FormatHelpers.FormatPreSelectMarkup(w));
            }
        }

        AnsiConsole.Write(table);

        var preSelected = scanResult.Worktrees.Where(w => w.IsPreSelected).ToList();
        var dirtyPreSelected = preSelected.Count(w => w.Status == WorktreeGitStatus.Dirty);
        var reclaimableBytes = preSelected.Sum(w => w.SizeBytes);

        var dirtySummary = dirtyPreSelected > 0
            ? $"[bold red]{dirtyPreSelected} DIRTY[/]"
            : "[green]0 DIRTY[/]";

        var sizeSummary = settings.NoSize
            ? "[grey]skipped (--no-size)[/]"
            : $"[bold cyan]{Markup.Escape(FormatHelpers.FormatSize(reclaimableBytes))}[/] [grey]({Markup.Escape(FormatHelpers.FormatDecimalGb(reclaimableBytes))})[/]";

        AnsiConsole.MarkupLine(
            $"Summary: [bold]{scanResult.Worktrees.Count}[/] total  [grey]|[/]  Pre-selected: [bold]{preSelected.Count}[/] ({dirtySummary})  [grey]|[/]  Est. reclaimable: {sizeSummary}");
        AnsiConsole.WriteLine();
    }

    private static IReadOnlyList<WorktreeInfo> PromptForSelection(
        IReadOnlyList<WorktreeInfo> removable,
        string repoRoot,
        bool noSize)
    {
        var maxBranchWidth = Math.Clamp(removable.Max(w => w.Branch.Length), 10, 32);
        var maxReasonWidth = Math.Clamp(removable.Max(w => w.Reason.Length), 8, 22);
        var terminalWidth = Math.Max(AnsiConsole.Profile.Width, 80);

        var prompt = new MultiSelectionPrompt<WorktreeInfo>()
            .Title("Select worktrees to [bold red]remove[/]:")
            .NotRequired()
            .PageSize(Math.Clamp(removable.Count, 10, 20))
            .MoreChoicesText("[grey](Move up and down to reveal more worktrees)[/]")
            .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm selection)[/]")
            .UseConverter(w => FormatChoiceLine(w, repoRoot, maxBranchWidth, maxReasonWidth, terminalWidth, noSize));

        foreach (var item in removable)
        {
            prompt.AddChoice(item);
            if (item.IsPreSelected)
            {
                prompt.Select(item);
            }
        }

        return AnsiConsole.Prompt(prompt);
    }

    private static string FormatChoiceLine(
        WorktreeInfo w,
        string repoRoot,
        int branchColWidth,
        int reasonColWidth,
        int terminalWidth,
        bool noSize)
    {
        var indexText = $"#{w.Index,-2}";
        var branchText = FormatHelpers.Truncate(w.Branch, branchColWidth).PadRight(branchColWidth);
        var statusPlain = FormatHelpers.FormatStatusPlain(w);
        var statusPad = new string(' ', Math.Max(0, 14 - statusPlain.Length));
        var statusMarkup = FormatHelpers.FormatStatusMarkup(w) + statusPad;
        var sizeText = (noSize ? "-" : FormatHelpers.FormatSize(w.SizeBytes, w.SizeCalculated)).PadLeft(10);
        var reasonText = FormatHelpers.Truncate(w.Reason, reasonColWidth).PadRight(reasonColWidth);

        // Reserve space for Spectre checkbox prefix (" [X] "), separators, and fixed columns so the line never wraps.
        var usedWidth = 6 + indexText.Length + 2 + branchColWidth + 3 + 14 + 3 + 10 + 3 + reasonColWidth + 3;
        var remainingForPath = terminalWidth - usedWidth - 2;

        var baseLine =
            $"[grey]{Markup.Escape(indexText)}[/]  " +
            $"[white]{Markup.Escape(branchText)}[/] [grey]|[/] " +
            $"{statusMarkup} [grey]|[/] " +
            $"[cyan]{Markup.Escape(sizeText)}[/] [grey]|[/] " +
            $"[grey]{Markup.Escape(reasonText)}[/]";

        if (remainingForPath < 12)
        {
            return baseLine;
        }

        var shortPath = FormatHelpers.ShortenPath(w.Path, repoRoot);
        var truncatedPath = FormatHelpers.TruncateStart(shortPath, remainingForPath);
        return $"{baseLine} [grey]|[/] [dim]{Markup.Escape(truncatedPath)}[/]";
    }

    private static bool ConfirmDeletion(IReadOnlyList<WorktreeInfo> selected, bool noSize)
    {
        var dirtyItems = selected.Where(w => w.Status == WorktreeGitStatus.Dirty).ToList();
        var lockedItems = selected.Where(w => w.IsLocked).ToList();
        var missingCount = selected.Count(w => !w.Exists);
        var existingCount = selected.Count - missingCount;
        var estBytes = selected.Sum(w => w.SizeBytes);

        AnsiConsole.WriteLine();
        var sizePart = noSize
            ? ""
            : $" (est. [bold cyan]{Markup.Escape(FormatHelpers.FormatSize(estBytes))}[/])";
        AnsiConsole.MarkupLine(
            $"Selected [bold]{selected.Count}[/] worktree(s): [red]{existingCount} to remove[/], [yellow]{missingCount} to prune[/]{sizePart}.");

        if (dirtyItems.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[bold red]WARNING:[/] [red]{dirtyItems.Count} selected worktree(s) have uncommitted changes (DIRTY):[/] " +
                $"{Markup.Escape(string.Join(", ", dirtyItems.Select(w => w.Branch)))}");
        }

        if (lockedItems.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[bold magenta]NOTE:[/] [magenta]{lockedItems.Count} selected worktree(s) are LOCKED and will be force-removed:[/] " +
                $"{Markup.Escape(string.Join(", ", lockedItems.Select(w => w.Branch)))}");
        }

        return AnsiConsole.Confirm(
            "Proceed with removing the selected worktrees?",
            defaultValue: dirtyItems.Count == 0 && lockedItems.Count == 0);
    }

    private static async Task<CleanupReport> DeleteWithProgressAsync(
        string repoRoot,
        IReadOnlyList<WorktreeInfo> selected,
        CancellationToken cancellationToken)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            return await WorktreeDeleter.DeleteAsync(repoRoot, selected, cancellationToken: cancellationToken);
        }

        CleanupReport? report = null;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new SpinnerColumn(),
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new ElapsedTimeColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[red]Removing selected worktrees...[/]", maxValue: Math.Max(selected.Count, 1));

                report = await WorktreeDeleter.DeleteAsync(
                    repoRoot,
                    selected,
                    onProgress: (current, total, wt) =>
                    {
                        var action = wt.Exists ? "Removing" : "Queueing prune";
                        task.Description = $"[red]{action}[/] [white]{Markup.Escape(FormatHelpers.Truncate(wt.Branch, 30))}[/] [grey]({current}/{total})[/]";
                        task.Value = current - 1;
                    },
                    cancellationToken: cancellationToken);

                task.Value = task.MaxValue;
                task.Description = report.HasFailures
                    ? "[yellow]Finished with some errors[/]"
                    : "[green]Cleanup completed[/]";
                task.StopTask();
            });

        return report!;
    }

    private static void RenderFinalReport(CleanupReport report, string repoRoot, bool noSize)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold]Cleanup Report[/]").LeftJustified());

        var reclaimedDisplay = noSize
            ? "[grey]not measured (--no-size)[/]"
            : $"[bold green]{Markup.Escape(FormatHelpers.FormatSize(report.TotalReclaimedBytes))}[/] [grey]({Markup.Escape(FormatHelpers.FormatDecimalGb(report.TotalReclaimedBytes))})[/]";

        AnsiConsole.MarkupLine(
            $"[green]Removed:[/] [bold]{report.RemovedCount}[/]  [grey]|[/]  " +
            $"[yellow]Pruned:[/] [bold]{report.PrunedCount}[/]  [grey]|[/]  " +
            $"[darkorange]Partial:[/] [bold]{report.PartialCount}[/]  [grey]|[/]  " +
            $"[red]Failed:[/] [bold]{report.FailedCount}[/]  [grey]|[/]  " +
            $"Reclaimed: {reclaimedDisplay}");

        var failures = report.Results
            .Where(r => r.Outcome is RemovalOutcome.Failed or RemovalOutcome.PartiallyRemoved)
            .ToList();

        if (failures.Count > 0 || (report.PruneAttempted && !report.PruneSucceeded))
        {
            AnsiConsole.WriteLine();
            var errTable = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Red)
                .Title("[bold red]Failed Operations (Bulk Error Report)[/]")
                .AddColumn("[bold]Branch / Operation[/]")
                .AddColumn("[bold]Path[/]")
                .AddColumn("[bold]Outcome[/]")
                .AddColumn("[bold]Error[/]");

            foreach (var f in failures)
            {
                var outcomeMarkup = f.Outcome == RemovalOutcome.PartiallyRemoved
                    ? "[darkorange]Partial[/]"
                    : "[bold red]Failed[/]";

                errTable.AddRow(
                    Markup.Escape(f.Worktree.Branch),
                    Markup.Escape(FormatHelpers.ShortenPath(f.Worktree.Path, repoRoot)),
                    outcomeMarkup,
                    $"[red]{Markup.Escape(f.ErrorMessage ?? "Unknown error")}[/]");
            }

            if (report.PruneAttempted && !report.PruneSucceeded)
            {
                errTable.AddRow(
                    "[yellow]git worktree prune[/]",
                    Markup.Escape(repoRoot),
                    "[bold red]Failed[/]",
                    $"[red]{Markup.Escape(report.PruneError ?? "Unknown prune error")}[/]");
            }

            AnsiConsole.Write(errTable);
        }
    }
}

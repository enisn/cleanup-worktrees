using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CleanupWorktrees.Commands;

public sealed class CleanupSettings : CommandSettings
{
    [CommandArgument(0, "[work-dir]")]
    [Description("Target git repository or worktree directory (default: current directory).")]
    public string? WorkDir { get; init; }

    [CommandOption("-d|--days <DAYS>")]
    [DefaultValue(1.0)]
    [Description("Pre-select worktrees inactive for more than N days (default: 1).")]
    public double Days { get; init; } = 1.0;

    [CommandOption("-c|--clean-only")]
    [Description("Only pre-select clean (and missing) worktrees; leave DIRTY worktrees unchecked.")]
    public bool CleanOnly { get; init; }

    [CommandOption("-e|--exclude <PATTERN>")]
    [Description("Exclude branch name, folder name, or full path (repeatable, supports * wildcards).")]
    public string[] Excludes { get; init; } = [];

    [CommandOption("-a|--all")]
    [Description("Pre-select all removable worktrees regardless of age.")]
    public bool All { get; init; }

    [CommandOption("--missing-only")]
    [Description("Only pre-select worktrees missing on disk (prunable).")]
    public bool MissingOnly { get; init; }

    [CommandOption("--no-size")]
    [Description("Skip directory size calculation for faster scanning.")]
    public bool NoSize { get; init; }

    [CommandOption("--dry-run")]
    [Description("Display the worktree table and exit without prompting or deleting.")]
    public bool DryRun { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Skip interactive prompts and immediately delete all pre-selected worktrees.")]
    public bool Yes { get; init; }

    public override ValidationResult Validate()
    {
        if (Days < 0 || double.IsNaN(Days) || double.IsInfinity(Days))
        {
            return ValidationResult.Error("--days must be a non-negative number.");
        }

        if (All && MissingOnly)
        {
            return ValidationResult.Error("--all and --missing-only cannot be used together.");
        }

        return ValidationResult.Success();
    }
}

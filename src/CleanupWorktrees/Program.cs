using CleanupWorktrees.Commands;
using Spectre.Console.Cli;

var app = new CommandApp<CleanupCommand>();

app.Configure(config =>
{
    config.SetApplicationName("cleanup-worktrees");
    config.SetApplicationVersion("1.0.0");
    config.AddExample();
    config.AddExample("/path/to/repo");
    config.AddExample("--days", "7", "--clean-only");
    config.AddExample("/path/to/repo", "--days", "3", "--exclude", "feature-*", "--dry-run");
});

return await app.RunAsync(args);

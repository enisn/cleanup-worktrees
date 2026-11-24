using System.Diagnostics;

namespace CleanupWorktrees.Services;

public sealed record GitCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public string CombinedError =>
        !string.IsNullOrWhiteSpace(StdErr) ? StdErr.Trim() :
        !string.IsNullOrWhiteSpace(StdOut) ? StdOut.Trim() :
        $"git exited with code {ExitCode}";
}

public static class GitRunner
{
    public static async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new GitCommandResult(-1, string.Empty, "Failed to start git process.");
            }

            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;

            return new GitCommandResult(process.ExitCode, stdOut, stdErr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GitCommandResult(-1, string.Empty, ex.Message);
        }
    }
}

using System.Globalization;
using System.IO.Enumeration;
using CleanupWorktrees.Models;
using Spectre.Console;

namespace CleanupWorktrees.Services;

public static class FormatHelpers
{
    private static readonly string[] SizeUnits = ["B", "KiB", "MiB", "GiB", "TiB"];

    public static string FormatSize(long bytes, bool calculated = true)
    {
        if (!calculated)
        {
            return "-";
        }

        if (bytes <= 0)
        {
            return "0 B";
        }

        double value = bytes;
        foreach (var unit in SizeUnits)
        {
            if (value < 1024.0 || unit == SizeUnits[^1])
            {
                return unit == "B"
                    ? $"{(long)value} B"
                    : $"{value.ToString("F2", CultureInfo.InvariantCulture)} {unit}";
            }

            value /= 1024.0;
        }

        return $"{bytes} B";
    }

    public static string FormatDecimalGb(long bytes)
    {
        var gb = bytes / 1_000_000_000.0;
        return $"{gb.ToString("F2", CultureInfo.InvariantCulture)} GB";
    }

    public static string FormatDate(DateTimeOffset? date)
    {
        if (date is null)
        {
            return "N/A";
        }

        return date.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static string ShortenPath(string fullPath, string? mainRepoRoot)
    {
        if (!string.IsNullOrEmpty(mainRepoRoot))
        {
            var normalizedMain = mainRepoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath, normalizedMain, StringComparison.Ordinal))
            {
                return ".";
            }

            var prefix = normalizedMain + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return fullPath[prefix.Length..];
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var normalizedHome = home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.StartsWith(normalizedHome + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return "~" + fullPath[normalizedHome.Length..];
            }
        }

        return fullPath;
    }

    public static string Truncate(string text, int maxLength)
    {
        if (maxLength <= 1)
        {
            return text.Length <= maxLength ? text : "…";
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)] + "…";
    }

    public static string TruncateStart(string text, int maxLength)
    {
        if (maxLength <= 1)
        {
            return text.Length <= maxLength ? text : "…";
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return "…" + text[^(maxLength - 1)..];
    }

    public static string FormatStatusMarkup(WorktreeInfo w)
    {
        var baseStatus = w.Status switch
        {
            WorktreeGitStatus.Clean => "[green]clean[/]",
            WorktreeGitStatus.Dirty => "[bold red]DIRTY[/]",
            WorktreeGitStatus.Missing => "[yellow]MISSING[/]",
            _ => "[red]ERROR[/]"
        };

        return w.IsLocked ? $"{baseStatus} [magenta]LOCKED[/]" : baseStatus;
    }

    public static string FormatStatusPlain(WorktreeInfo w)
    {
        var baseStatus = w.Status switch
        {
            WorktreeGitStatus.Clean => "clean",
            WorktreeGitStatus.Dirty => "DIRTY",
            WorktreeGitStatus.Missing => "MISSING",
            _ => "ERROR"
        };

        return w.IsLocked ? $"{baseStatus} LOCKED" : baseStatus;
    }

    public static string FormatPreSelectMarkup(WorktreeInfo w)
    {
        var reason = Markup.Escape(w.Reason);
        if (w.IsPreSelected)
        {
            var color = w.Status switch
            {
                WorktreeGitStatus.Dirty => "red",
                WorktreeGitStatus.Missing => "yellow",
                _ => "green"
            };
            return $"[bold {color}]Yes[/] [grey]({reason})[/]";
        }

        return $"[grey]No ({reason})[/]";
    }

    public static bool MatchesExclude(string pattern, WorktreeInfo candidate)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var trimmed = pattern.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = candidate.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var basename = Path.GetFileName(normalizedPath);

        if (trimmed.Contains('*') || trimmed.Contains('?'))
        {
            return FileSystemName.MatchesSimpleExpression(trimmed, basename, ignoreCase: true)
                || FileSystemName.MatchesSimpleExpression(trimmed, candidate.Branch, ignoreCase: true)
                || FileSystemName.MatchesSimpleExpression(trimmed, normalizedPath, ignoreCase: true);
        }

        return string.Equals(trimmed, normalizedPath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, basename, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, candidate.Branch, StringComparison.OrdinalIgnoreCase);
    }
}

using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Backlog;
using PPDS.Cli.Services.Session;
using Spectre.Console;

namespace PPDS.Cli.Commands.Backlog;

/// <summary>
/// Command group for backlog operations.
/// </summary>
public static class BacklogCommandGroup
{
    public static Command Create()
    {
        var fullOption = new Option<bool>("--full")
        {
            Description = "Show all issues in each category, not just top 5"
        };

        var bugsOption = new Option<bool>("--bugs")
        {
            Description = "Show only bugs"
        };

        var readyOption = new Option<bool>("--ready")
        {
            Description = "Show only ready-for-work items"
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Cross-repo ecosystem view"
        };

        var repoOption = new Option<string?>("--repo")
        {
            Description = "Override repository (owner/repo format)"
        };

        var noCacheOption = new Option<bool>("--no-cache")
        {
            Description = "Bypass cache and fetch fresh data"
        };

        var command = new Command("backlog", "View project backlog: bugs, ready work, and project health")
        {
            fullOption,
            bugsOption,
            readyOption,
            allOption,
            repoOption,
            noCacheOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var full = parseResult.GetValue(fullOption);
            var bugsOnly = parseResult.GetValue(bugsOption);
            var readyOnly = parseResult.GetValue(readyOption);
            var allRepos = parseResult.GetValue(allOption);
            var repo = parseResult.GetValue(repoOption);
            var noCache = parseResult.GetValue(noCacheOption);

            return await ExecuteAsync(
                globalOptions,
                full,
                bugsOnly,
                readyOnly,
                allRepos,
                repo,
                noCache,
                cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        GlobalOptionValues globalOptions,
        bool full,
        bool bugsOnly,
        bool readyOnly,
        bool allRepos,
        string? repo,
        bool noCache,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Create service with optional session integration
            ISessionService? sessionService = null;
            try
            {
                var spawner = new WindowsTerminalWorkerSpawner();
                var sessionLogger = NullLogger<SessionService>.Instance;
                sessionService = new SessionService(spawner, sessionLogger);
            }
            catch
            {
                // Session service not available, continue without it
            }

            var logger = NullLogger<BacklogService>.Instance;
            var service = new BacklogService(sessionService, logger);

            // Parse repo option if provided
            string? owner = null;
            string? repoName = null;
            if (!string.IsNullOrEmpty(repo))
            {
                var parts = repo.Split('/');
                if (parts.Length == 2)
                {
                    owner = parts[0];
                    repoName = parts[1];
                }
            }

            if (allRepos)
            {
                var ecosystem = await service.GetEcosystemBacklogAsync(noCache, cancellationToken);

                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(ecosystem);
                }
                else
                {
                    RenderEcosystemBacklog(ecosystem, full, bugsOnly, readyOnly);
                }
            }
            else
            {
                var backlog = await service.GetBacklogAsync(owner, repoName, noCache, cancellationToken);

                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(backlog);
                }
                else
                {
                    RenderBacklog(backlog, full, bugsOnly, readyOnly);
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "fetching backlog", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void RenderBacklog(BacklogData backlog, bool full, bool bugsOnly, bool readyOnly)
    {
        var rule = new Rule($"[bold blue]BACKLOG: {backlog.Repo}[/]")
        {
            Justification = Justify.Left
        };
        AnsiConsole.Write(rule);

        if (backlog.FromCache)
        {
            var age = (int)(DateTimeOffset.UtcNow - backlog.FetchedAt).TotalMinutes;
            Console.Error.WriteLine($"(cached {age}m ago)");
        }

        Console.WriteLine();

        // Health metrics
        if (!bugsOnly && !readyOnly)
        {
            RenderHealthMetrics(backlog.Health);
            Console.WriteLine();
        }

        // Bugs
        if (!readyOnly)
        {
            RenderCategory("BUGS", backlog.Bugs, full, item =>
                $"[red]#{item.Number}[/] [[{EscapeMarkup(item.Priority ?? "?")}]] {EscapeMarkup(item.Title)} [dim]({item.AgeInDays}d)[/]" +
                (item.ActiveSessionId != null ? $" [yellow][[{item.ActiveSessionStatus}]][/]" : ""));
        }

        // In Progress
        if (!bugsOnly && !readyOnly)
        {
            RenderCategory("IN PROGRESS", backlog.InProgress, full, item =>
            {
                var assignee = item.Assignee != null ? $"@{item.Assignee}" : "unassigned";
                return $"[yellow]#{item.Number}[/] [[{EscapeMarkup(item.Size ?? "?")}]] {EscapeMarkup(item.Title)} [dim]{assignee} ({item.AgeInDays}d)[/]" +
                       (item.ActiveSessionId != null ? $" [green][[{item.ActiveSessionStatus}]][/]" : "");
            });
        }

        // Blocked
        if (!bugsOnly && !readyOnly)
        {
            RenderCategory("BLOCKED", backlog.Blocked, full, item =>
                $"[grey]#{item.Number}[/] [[{EscapeMarkup(item.Size ?? "?")}]] {EscapeMarkup(item.Title)} [dim]({item.AgeInDays}d)[/]");
        }

        // Ready
        if (!bugsOnly)
        {
            RenderCategory("READY FOR WORK", backlog.Ready, full, item =>
                $"[green]#{item.Number}[/] [[{EscapeMarkup(item.Priority ?? "?")}, {EscapeMarkup(item.Size ?? "?")}]] {EscapeMarkup(item.Title)}" +
                (item.ActiveSessionId != null ? $" [yellow][[{item.ActiveSessionStatus}]][/]" : ""));
        }

        // Untriaged
        if (!bugsOnly && !readyOnly)
        {
            if (backlog.Untriaged.Count > 0)
            {
                Console.WriteLine($"[dim]UNTRIAGED ({backlog.Untriaged.Count})[/]");
                Console.WriteLine($"  {backlog.Untriaged.Count} issues need triage (missing Size or Target)");
                Console.WriteLine("  Run: /triage");
                Console.WriteLine();
            }
        }

        // PRs
        if (!bugsOnly && !readyOnly && backlog.PullRequests.Count > 0)
        {
            AnsiConsole.MarkupLine($"[blue]OPEN PRs ({backlog.PullRequests.Count})[/]");
            var prsToShow = full ? backlog.PullRequests : backlog.PullRequests.Take(5).ToList();
            foreach (var pr in prsToShow)
            {
                var status = pr.IsDraft ? "[dim](Draft)[/]" : "[green](Review)[/]";
                AnsiConsole.MarkupLine($"  #{pr.Number} {EscapeMarkup(pr.Title)} @{pr.Author} {status}");
            }
            if (!full && backlog.PullRequests.Count > 5)
            {
                Console.WriteLine($"  ... +{backlog.PullRequests.Count - 5} more (use --full to see all)");
            }
            Console.WriteLine();
        }

        // Tip
        AnsiConsole.MarkupLine("[dim]TIP: Use ppds backlog --help to see all options[/]");
    }

    private static void RenderEcosystemBacklog(EcosystemBacklog ecosystem, bool full, bool bugsOnly, bool readyOnly)
    {
        var rule = new Rule("[bold blue]BACKLOG: PPDS Ecosystem[/]")
        {
            Justification = Justify.Left
        };
        AnsiConsole.Write(rule);
        Console.WriteLine();

        // Summary table
        var table = new Table();
        table.AddColumn("Repository");
        table.AddColumn("Open", c => c.RightAligned());
        table.AddColumn("Bugs", c => c.RightAligned());
        table.AddColumn("In Progress", c => c.RightAligned());
        table.AddColumn("Ready", c => c.RightAligned());

        foreach (var repo in ecosystem.Repos)
        {
            table.AddRow(
                repo.Repo,
                repo.Health.OpenIssues.ToString(),
                repo.Health.TotalBugs.ToString(),
                repo.Health.InProgressCount.ToString(),
                repo.Health.ReadyCount.ToString());
        }

        table.AddRow(
            "[bold]TOTAL[/]",
            $"[bold]{ecosystem.Health.OpenIssues}[/]",
            $"[bold]{ecosystem.Health.TotalBugs}[/]",
            $"[bold]{ecosystem.Health.InProgressCount}[/]",
            $"[bold]{ecosystem.Health.ReadyCount}[/]");

        AnsiConsole.Write(table);
        Console.WriteLine();

        // Aggregate bugs across repos
        if (!readyOnly)
        {
            var allBugs = ecosystem.Repos.SelectMany(r => r.Bugs).OrderBy(b => GetPriorityOrder(b.Priority)).ToList();
            RenderCategory("ALL BUGS", allBugs, full, item =>
                $"[dim][[{GetRepoShortName(item.Repository)}]][/] [red]#{item.Number}[/] [[{EscapeMarkup(item.Priority ?? "?")}]] {EscapeMarkup(item.Title)}");
        }

        // Aggregate ready items
        if (!bugsOnly)
        {
            var allReady = ecosystem.Repos.SelectMany(r => r.Ready).OrderBy(r => GetPriorityOrder(r.Priority)).ToList();
            RenderCategory("TOP READY ITEMS", allReady, full, item =>
                $"[dim][[{GetRepoShortName(item.Repository)}]][/] [green]#{item.Number}[/] [[{EscapeMarkup(item.Priority ?? "?")}, {EscapeMarkup(item.Size ?? "?")}]] {EscapeMarkup(item.Title)}");
        }
    }

    private static void RenderHealthMetrics(HealthMetrics health)
    {
        AnsiConsole.MarkupLine("[bold]PROJECT HEALTH[/]");

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddColumn();

        grid.AddRow(
            $"Open Issues: [bold]{health.OpenIssues}[/]",
            $"In Progress: [bold]{health.InProgressCount}[/]",
            $"PRs Open: [bold]{health.OpenPrs}[/]");

        var bugColor = health.CriticalBugs > 0 ? "red" : (health.HighPriorityBugs > 0 ? "yellow" : "green");
        var bugDetail = health.CriticalBugs > 0 ? $"({health.CriticalBugs} critical)" : "";

        grid.AddRow(
            $"Bugs: [{bugColor}]{health.TotalBugs}[/] {bugDetail}",
            $"Ready: [bold]{health.ReadyCount}[/]",
            $"Blocked: [bold]{health.BlockedCount}[/]");

        AnsiConsole.Write(grid);
    }

    private static void RenderCategory(string title, IReadOnlyList<BacklogItem> items, bool full, Func<BacklogItem, string> formatter)
    {
        if (items.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine($"[bold]{title} ({items.Count})[/]");

        var itemsToShow = full ? items : items.Take(5).ToList();
        foreach (var item in itemsToShow)
        {
            AnsiConsole.MarkupLine($"  {formatter(item)}");
        }

        if (!full && items.Count > 5)
        {
            Console.WriteLine($"  ... +{items.Count - 5} more (use --full to see all)");
        }

        Console.WriteLine();
    }

    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    private static int GetPriorityOrder(string? priority)
    {
        return priority?.ToUpperInvariant() switch
        {
            "P0-CRITICAL" or "P0" => 0,
            "P1-HIGH" or "P1" => 1,
            "P2-MEDIUM" or "P2" => 2,
            "P3-LOW" or "P3" => 3,
            _ => 99
        };
    }

    private static string GetRepoShortName(string repository)
    {
        // "joshsmithxrm/power-platform-developer-suite" -> "ppds"
        // "joshsmithxrm/ppds-docs" -> "docs"
        var name = repository.Split('/').LastOrDefault() ?? repository;
        if (name == "power-platform-developer-suite")
        {
            return "ppds";
        }
        if (name.StartsWith("ppds-"))
        {
            return name["ppds-".Length..];
        }
        return name;
    }
}

using System.Text.Json.Nodes;
using Birchpad.Core.Mac;

namespace Birchpad.Core.Codex;

/// <summary>Usage against one Codex rate-limit window.</summary>
public sealed record CodexLimit(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);

/// <summary>The 5-hour and weekly Codex limits, plus plan, credits, and account history when available.</summary>
public sealed record CodexUsage(CodexLimit? Primary, CodexLimit? Secondary, DateTimeOffset FetchedAt)
{
    public string? Plan { get; init; }

    public double? CreditBalance { get; init; }

    public bool HasCredits { get; init; }

    public long? LifetimeTokens { get; init; }

    public long? PeakDailyTokens { get; init; }

    public int? CurrentStreakDays { get; init; }

    public int? LongestStreakDays { get; init; }
}

/// <summary>A Codex thread as the app server lists it.</summary>
public sealed record CodexThread(string Id, string? Name, string? Cwd, DateTimeOffset? UpdatedAt);

/// <summary>
/// Talks JSON-RPC to <c>codex app-server</c> over stdio, one short-lived process per call.
/// The app server reads local state and account data; it makes no model calls.
/// </summary>
public sealed class CodexAppServer
{
    private readonly string codexPath;

    public CodexAppServer(string codexPath)
    {
        this.codexPath = codexPath;
    }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public static string? FindCodex()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates = ["/opt/homebrew/bin/codex", "/usr/local/bin/codex", Path.Combine(home, ".local", "bin", "codex")];
        return candidates.FirstOrDefault(File.Exists);
    }

    public CodexUsage? ReadUsage(DateTimeOffset now)
    {
        var limits = this.Request("account/rateLimits/read", new JsonObject())?["rateLimits"];
        if (limits is null)
        {
            return null;
        }

        var usage = ParseUsage(limits, now);
        var summary = this.Request("account/usage/read", new JsonObject())?["summary"];
        return summary is null ? usage : WithSummary(usage, summary);
    }

    internal static CodexUsage WithSummary(CodexUsage usage, JsonNode summary) => usage with
    {
        LifetimeTokens = Long(summary["lifetimeTokens"]),
        PeakDailyTokens = Long(summary["peakDailyTokens"]),
        CurrentStreakDays = (int?)Long(summary["currentStreakDays"]),
        LongestStreakDays = (int?)Long(summary["longestStreakDays"]),
    };

    private static long? Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    public IReadOnlyList<CodexThread> ListThreads(int limit = 50)
    {
        var data = this.Request("thread/list", new JsonObject { ["limit"] = limit })?["data"] as JsonArray;
        return data is null ? [] : ParseThreads(data);
    }

    internal static CodexUsage ParseUsage(JsonNode limits, DateTimeOffset now) =>
        new(ParseLimit(limits["primary"]), ParseLimit(limits["secondary"]), now)
        {
            Plan = limits["planType"] is JsonValue plan ? plan.ToString() : null,
            HasCredits = limits["credits"]?["hasCredits"] is JsonValue has && has.TryGetValue<bool>(out var h) && h,
            CreditBalance = limits["credits"]?["balance"] is JsonValue balance && balance.TryGetValue<double>(out var b) ? b : null,
        };

    internal static IReadOnlyList<CodexThread> ParseThreads(JsonArray data) =>
        data.OfType<JsonObject>()
            .Where(t => t["id"] is JsonValue)
            .Select(t => new CodexThread(
                t["id"]!.ToString(),
                t["name"] is JsonValue n ? n.ToString() : null,
                t["cwd"] is JsonValue c ? c.ToString() : null,
                t["updatedAt"] is JsonValue u && u.TryGetValue<long>(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null))
            .ToList();

    private static CodexLimit? ParseLimit(JsonNode? node)
    {
        if (node is not JsonObject obj || obj["usedPercent"] is not JsonValue used)
        {
            return null;
        }

        var window = obj["windowDurationMins"] is JsonValue w && w.TryGetValue<int>(out var minutes) ? minutes : 0;
        DateTimeOffset? resets = obj["resetsAt"] is JsonValue r && r.TryGetValue<long>(out var at) ? DateTimeOffset.FromUnixTimeSeconds(at) : null;
        return new CodexLimit(used.GetValue<double>(), window, resets);
    }

    /// <summary>Starts the app server, initializes, sends one request, and returns its result or null.</summary>
    private JsonNode? Request(string method, JsonObject parameters)
    {
        // Its own session: the app server runs git to check git-sourced plugin marketplaces, and that git once
        // hung up the whole process group, which was the plugin service's.
        using var process = ChildProcess.Start(this.codexPath, ["app-server"], redirectInput: true, captureError: false);
        try
        {
            Send(process,new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "birchpad", ["version"] = "0.1.0" } } });
            if (ReadResponse(process, 1, this.Timeout) is null)
            {
                return null;
            }

            Send(process, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "initialized", ["params"] = new JsonObject() });
            Send(process, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = method, ["params"] = parameters });
            return ReadResponse(process, 2, this.Timeout)?["result"];
        }
        finally
        {
            // Also ends any git the app server left running, since it shares the app server's process group.
            process.Kill();
        }
    }

    private static void Send(ChildProcess process, JsonObject message)
    {
        process.StandardInput!.WriteLine(message.ToJsonString());
        process.StandardInput.Flush();
    }

    private static JsonObject? ReadResponse(ChildProcess process, int id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var read = process.StandardOutput.ReadLineAsync();
            if (!read.Wait(deadline - DateTime.UtcNow) || read.Result is null)
            {
                return null;
            }

            try
            {
                // Notifications arrive between responses; skip anything that is not our answer.
                if (JsonNode.Parse(read.Result) is JsonObject message && message["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id)
                {
                    return message;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not a protocol line; keep reading.
            }
        }

        return null;
    }
}

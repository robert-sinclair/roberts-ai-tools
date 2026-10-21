using System.Text.Json;
using System.Text.Json.Serialization;
using rNotice.Core.Rules;
using rNotice.Core.Scheduling;

namespace rNotice.Agent;

public sealed record QuietHoursSetting(TimeOnly Start, TimeOnly End);

/// <summary>
/// <c>%LOCALAPPDATA%\rNotice\settings.json</c>. Written with defaults on first run so every
/// setting is visible and editable. Read at startup; restart the agent after editing.
/// </summary>
public sealed record AgentSettings
{
    public string WorkbookFolder { get; init; } = "efficiency";
    public string WorkbookFile { get; init; } = "rNotice.xlsx";

    public int CalendarPollSeconds { get; init; } = 60;
    public int WorkbookPollSeconds { get; init; } = 300;
    public int RetentionDays { get; init; } = 30;
    public int SnoozeMinutes { get; init; } = 15;
    public int TakeoverGraceSeconds { get; init; } = 300;

    /// <summary>Local time. Null for none. Meeting alerts ignore it.</summary>
    public QuietHoursSetting? QuietHours { get; init; }

    /// <summary>Null until the first run decides. The first run turns it on.</summary>
    public bool? StartWithWindows { get; init; }

    public RulesOptions Rules { get; init; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public SchedulerOptions ToSchedulerOptions() => new()
    {
        TakeoverGraceSeconds = TakeoverGraceSeconds,
        QuietHours = QuietHours is { } q ? new QuietHours(q.Start, q.End, TimeZoneInfo.Local) : null
    };

    /// <summary>
    /// A broken file is logged and replaced in memory by defaults — never overwritten, so a
    /// typo doesn't cost the rest of the user's edits.
    /// </summary>
    public static AgentSettings Load()
    {
        try
        {
            if (File.Exists(Paths.Settings))
                return JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(Paths.Settings), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {Paths.Settings}; using defaults until it is fixed", ex);
            return new AgentSettings { StartWithWindows = false };
        }

        var defaults = new AgentSettings();
        defaults.Save();
        return defaults;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write {Paths.Settings}", ex);
        }
    }
}

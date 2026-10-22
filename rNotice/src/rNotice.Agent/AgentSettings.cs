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

    /// <summary>
    /// How often both sources are checked. One interval for both, so "next refresh" means one
    /// thing. Floor of 30 seconds — never tighten polling without measuring (SPEC.md, <i>Polling</i>).
    /// Replaces calendarPollSeconds and workbookPollSeconds; old keys in the file are ignored.
    /// </summary>
    public int RefreshSeconds { get; init; } = 60;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Max(30, RefreshSeconds));

    /// <summary>Read the inbox. Off means no mail calls at all.</summary>
    public bool MailEnabled { get; init; } = true;

    /// <summary>
    /// On the first sync, or after Graph drops the delta token, how far back the inbox is read.
    /// Older mail never enters the queue.
    /// </summary>
    public int MailBackfillDays { get; init; } = 3;

    /// <summary>How far back Sent Items is read to know which threads you've sent in.</summary>
    public int ReplyLookbackDays { get; init; } = 60;
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

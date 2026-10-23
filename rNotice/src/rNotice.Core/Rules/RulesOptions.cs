namespace rNotice.Core.Rules;

/// <summary>Who "you" are, as each source identifies you.</summary>
public sealed record Me(string UserId, string Address);

/// <summary>Rule configuration. Defaults follow SPEC.md.</summary>
public sealed record RulesOptions
{
    /// <summary>How long before a meeting starts the takeover fires. SPEC: "~1 min".</summary>
    public int TakeoverLeadSeconds { get; init; } = 60;

    /// <summary>
    /// Subject patterns for meetings that never alert. Plain text matches anywhere in the
    /// subject, case-insensitive. Wrap in slashes for a regex: <c>/^1:1 /</c>.
    /// </summary>
    public IReadOnlyList<string> MeetingBlocklist { get; init; } = [];

    /// <summary>Exact addresses, or <c>@domain.com</c> for a whole domain.</summary>
    public IReadOnlyList<string> IgnoreSenders { get; init; } = [];

    /// <summary>Same matching as <see cref="IgnoreSenders"/>.</summary>
    public IReadOnlyList<string> VipSenders { get; init; } = [];

    /// <summary>Group and meeting chats to watch for @mentions, by id. Picked in the agent.</summary>
    public IReadOnlyList<string> WatchedChatIds { get; init; } = [];

    /// <summary>Every one-on-one chat is watched, without picking. A 1:1 message is nearly always for you.</summary>
    public bool WatchOneOnOneChats { get; init; } = true;

    /// <summary>Workbook rows due further out than this are ignored.</summary>
    public int HorizonDays { get; init; } = 14;

    /// <summary>Used when a row's <c>LeadMinutes</c> is blank.</summary>
    public int DefaultLeadMinutes { get; init; } = 15;

    /// <summary>"Subject, sender, first ~500 characters."</summary>
    public int SnippetLength { get; init; } = 500;

    /// <summary>The category rNotice puts on events it promotes.</summary>
    public string PromotedCategory { get; init; } = "rNotice";
}

using System.Globalization;
using rNotice.Core.Items;
using rNotice.Core.Sources;

namespace rNotice.Core.Rules;

/// <summary>Either an item to store, or the reason the observation was skipped.</summary>
public sealed record IntakeResult(Item? Item, string? SkipReason)
{
    public bool Skipped => Item is null;

    public static IntakeResult Take(Item item) => new(item, null);
    public static IntakeResult Skip(string reason) => new(null, reason);
}

/// <summary>
/// Turns what a poller saw into an item, or decides it isn't one.
///
/// The deterministic rules from SPEC.md, Classification. Anything no rule claims gets the
/// fallback <c>P3</c>/<c>silent</c> with <see cref="ClassifiedBy.Default"/>, which is what the
/// model later picks up — and what the user keeps if the model is off.
///
/// Rules are first-match in the order written. Each method's comments list the order.
/// </summary>
public sealed class Intake(RulesOptions options, Me me)
{
    private readonly RulesOptions _options = options;
    private readonly Me _me = me;

    public static Classification Fallback(string why) =>
        new(Priority.P3, Interrupt.Silent, ClassifiedBy.Default, why);

    // ── Calendar ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every meeting is an item and takes over at T − <see cref="RulesOptions.TakeoverLeadSeconds"/>,
    /// minus the blocklist and events rNotice promoted itself.
    ///
    /// Also skipped, which SPEC.md doesn't say: cancelled instances, all-day events (a takeover
    /// at midnight helps nobody) and events already over.
    ///
    /// Meetings enter as <see cref="Stage.Planned"/>, not Intake. They are already scheduled;
    /// there is no decision left to make about them.
    /// </summary>
    public IntakeResult FromCalendar(CalendarEvent e, DateTimeOffset nowUtc)
    {
        if (e.IsCancelled) return IntakeResult.Skip("Cancelled");
        if (e.IsAllDay) return IntakeResult.Skip("All-day event");
        if (e.EndUtc <= nowUtc) return IntakeResult.Skip("Already over");

        if (e.Categories.Contains(_options.PromotedCategory, StringComparer.OrdinalIgnoreCase))
            return IntakeResult.Skip("Promoted by rNotice; its own item owns the alert");

        var blocked = TextMatch.FirstSubjectMatch(e.Subject, _options.MeetingBlocklist);
        if (blocked is not null) return IntakeResult.Skip($"Meeting blocklist: {blocked}");

        return IntakeResult.Take(new Item
        {
            Id = Guid.NewGuid(),
            Source = Source.Calendar,
            SourceId = e.Id,
            Stage = Stage.Planned,
            SourceRef = e.WebLink,
            Title = e.Subject,
            From = e.IsOrganizer ? null : e.Organizer,
            ReceivedUtc = nowUtc,
            DueUtc = e.StartUtc,
            FireAtUtc = e.StartUtc.AddSeconds(-_options.TakeoverLeadSeconds),
            JoinUrl = e.JoinUrl,
            Classification = new Classification(Priority.P1, Interrupt.Takeover, ClassifiedBy.Rule,
                $"Meeting; takes over {_options.TakeoverLeadSeconds}s before start")
        });
    }

    // ── Mail ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Skip: sent by you, meeting messages, ignored sender. Then: reply on a thread you've sent
    /// in → P1/toast; VIP sender → P1/alert; otherwise the fallback. Being the only To recipient
    /// steps the result up one priority.
    ///
    /// The reply rule is <c>conversationId</c> in a conversation you've sent in, received after
    /// your first send there. Checked against In-Reply-To/References on real mail in the
    /// 2026-10-21 spike — see SPEC.md. Without the time check, the message you replied to counts
    /// as a reply to you; without skipping meeting messages, every "Accepted:" does.
    /// </summary>
    /// <param name="firstSentByConversation">conversationId → when you first sent in it, from Sent Items.</param>
    public IntakeResult FromMail(MailMessage m, IReadOnlyDictionary<string, DateTimeOffset> firstSentByConversation)
    {
        if (m.FromAddress.Equals(_me.Address, StringComparison.OrdinalIgnoreCase))
            return IntakeResult.Skip("Sent by you");

        // Invites, responses and cancellations. The calendar source already covers the meeting.
        if (m.IsMeetingMessage)
            return IntakeResult.Skip("Meeting message");

        if (TextMatch.SenderMatches(m.FromAddress, _options.IgnoreSenders))
            return IntakeResult.Skip("Ignored sender");

        Classification c;
        if (firstSentByConversation.TryGetValue(m.ConversationId, out var firstSent) && m.ReceivedUtc > firstSent)
            c = new(Priority.P1, Interrupt.Toast, ClassifiedBy.Rule, "Reply on a thread you've sent in");
        else if (TextMatch.SenderMatches(m.FromAddress, _options.VipSenders))
            c = new(Priority.P1, Interrupt.Alert, ClassifiedBy.Rule, "VIP sender");
        else
            c = Fallback("No rule matched");

        var soleRecipient = m.To.Count == 1 && m.To[0].Equals(_me.Address, StringComparison.OrdinalIgnoreCase);
        if (soleRecipient && c.Priority != Priority.P1)
            c = c with { Priority = c.Priority.StepUp(), Reason = c.Reason + "; you're the only To recipient" };

        return IntakeResult.Take(new Item
        {
            Id = Guid.NewGuid(),
            Source = Source.Mail,
            SourceId = m.Id,
            SourceRef = m.WebLink,
            Title = m.Subject,
            Snippet = TextMatch.Truncate(m.BodyPreview, _options.SnippetLength),
            From = m.FromName is null ? m.FromAddress : $"{m.FromName} <{m.FromAddress}>",
            ReceivedUtc = m.ReceivedUtc,
            IsRead = m.IsRead,
            FireAtUtc = m.ReceivedUtc,
            Classification = c
        });
    }

    // ── Teams ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Skip: system messages, your own, unwatched chats. Watched means picked, or any
    /// one-on-one chat while <see cref="RulesOptions.WatchOneOnOneChats"/> is on.
    ///
    /// Then: you're @mentioned → P2/alert. A 1:1 message without one gets the fallback.
    /// A group or meeting message without one is skipped until the classifier (step 7) can
    /// judge it — decided 2026-10-23, since a busy group chat would put a dozen quiet items a
    /// day into Intake.
    ///
    /// A one-on-one chat is <b>one item per chat</b>, keyed by chat id: each new message
    /// updates it, so a back-and-forth is one thing to handle, not ten. A mention in a group
    /// chat is one item per message, keyed by chat and message, since message ids are only
    /// unique within a chat.
    /// </summary>
    /// <summary>Your own message in a one-on-one chat: the chat's item has been answered.</summary>
    public bool IsMyOneOnOneReply(ChatMessage m) =>
        !m.IsSystemMessage && m.ChatType == ChatType.OneOnOne && m.FromUserId == _me.UserId;

    public IntakeResult FromTeams(ChatMessage m)
    {
        if (m.IsSystemMessage) return IntakeResult.Skip("System message");
        if (m.FromUserId == _me.UserId) return IntakeResult.Skip("Sent by you");

        var oneOnOne = m.ChatType == ChatType.OneOnOne;
        var watched = (oneOnOne && _options.WatchOneOnOneChats) || _options.WatchedChatIds.Contains(m.ChatId);
        if (!watched) return IntakeResult.Skip("Chat not watched");

        Classification c;
        if (m.MentionedUserIds.Contains(_me.UserId))
            c = new Classification(Priority.P2, Interrupt.Alert, ClassifiedBy.Rule, "You were @mentioned");
        else if (oneOnOne)
            c = Fallback("One-on-one chat; no rule matched");
        else
            return IntakeResult.Skip("Group chat, no mention; waits for the classifier");

        var snippet = TextMatch.Truncate(m.TextPreview, _options.SnippetLength);

        return IntakeResult.Take(new Item
        {
            Id = Guid.NewGuid(),
            Source = Source.Teams,
            SourceId = oneOnOne ? m.ChatId : $"{m.ChatId}|{m.Id}",
            SourceRef = m.WebUrl,
            // Chat messages have no subject. The first line stands in for one.
            Title = TextMatch.Truncate(snippet?.Split('\n')[0], 80) ?? "(Teams message)",
            Snippet = snippet,
            From = oneOnOne || m.ChatName is null ? m.FromName : $"{m.FromName} in {m.ChatName}",
            ReceivedUtc = m.CreatedUtc,
            FireAtUtc = m.CreatedUtc,
            Classification = c
        });
    }

    // ── Workbook ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A row becomes an item if it has an <c>Id</c>, is pending, and has a parseable
    /// <c>DueUtc</c> inside the horizon. Its own <c>Priority</c> and <c>Interrupt</c> columns
    /// decide the classification; blanks default to P3 and alert.
    ///
    /// A blank <c>Status</c> counts as pending. SPEC.md says "Status not Pending → ignore", but
    /// <c>Status</c> is app-written, so a row you just typed has none — taken literally, the
    /// rule would ignore every new row.
    /// </summary>
    public IntakeResult FromWorkbook(WorkbookRow row, DateTimeOffset nowUtc)
    {
        var id = Text(row.Id);
        if (id is null) return IntakeResult.Skip("No Id");

        var status = Text(row.Status);
        if (status is not null && !status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            return IntakeResult.Skip($"Status is {status}");

        if (IsNumber(row.DueUtc))
            return IntakeResult.Skip("DueUtc is a number — Excel converted the date. Format the column as text.");

        var dueText = Text(row.DueUtc);
        if (dueText is null) return IntakeResult.Skip("No DueUtc");

        if (!DateTimeOffset.TryParse(dueText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var due))
            return IntakeResult.Skip($"DueUtc not ISO 8601: {dueText}");

        if (due > nowUtc.AddDays(_options.HorizonDays))
            return IntakeResult.Skip("Due beyond the horizon");

        var lead = _options.DefaultLeadMinutes;
        var leadText = Text(row.LeadMinutes);
        if (leadText is not null && int.TryParse(leadText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLead))
            lead = parsedLead;

        var notes = new List<string>();
        var priority = ParseEnum(Text(row.Priority), Priority.P3, "Priority", notes);
        var interrupt = ParseEnum(Text(row.Interrupt), Interrupt.Alert, "Interrupt", notes);

        var reason = "Workbook row; priority and interrupt from its columns";
        if (notes.Count > 0) reason += "; " + string.Join("; ", notes);

        var client = Text(row.Client);
        var title = Text(row.Title) ?? "(untitled row)";

        return IntakeResult.Take(new Item
        {
            Id = Guid.NewGuid(),
            Source = Source.Workbook,
            SourceId = id,
            SourceRef = row.WebUrl,
            Title = client is null ? title : $"{client}: {title}",
            Snippet = TextMatch.Truncate(Text(row.Details), _options.SnippetLength),
            ReceivedUtc = nowUtc,
            DueUtc = due,
            FireAtUtc = due.AddMinutes(-lead),
            Classification = new Classification(priority, interrupt, ClassifiedBy.Rule, reason)
        });
    }

    private static TEnum ParseEnum<TEnum>(string? text, TEnum fallback, string column, List<string> notes)
        where TEnum : struct, Enum
    {
        if (text is null) return fallback;
        // Enum.TryParse accepts numbers too ("7" → an undefined value), so check it's defined.
        if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            && !int.TryParse(text, out _))
            return value;

        notes.Add($"{column} '{text}' not recognised, used {fallback}");
        return fallback;
    }

    private static bool IsNumber(object? cell) =>
        cell is double or float or decimal or int or long;

    private static string? Text(object? cell)
    {
        var s = cell switch
        {
            null => null,
            string str => str,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => cell.ToString()
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}

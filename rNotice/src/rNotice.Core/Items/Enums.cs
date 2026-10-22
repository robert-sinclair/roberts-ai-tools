namespace rNotice.Core.Items;

public enum Source
{
    Workbook,
    Calendar,
    Mail,
    Teams
}

/// <summary>
/// The app sets the first stage: Intake for everything, except meetings, which are already
/// scheduled and enter as Planned. Everything after that is the user.
/// </summary>
public enum Stage
{
    Intake,
    Planned,
    Done,
    Dismissed
}

/// <summary>Where an item sits in the queue. Decides ordering and nothing else.</summary>
public enum Priority
{
    P1 = 1,
    P2 = 2,
    P3 = 3,
    P4 = 4
}

/// <summary>
/// How an item reaches the user, quietest first, so comparisons read naturally
/// (<c>interrupt &gt;= Interrupt.Alert</c>).
/// </summary>
public enum Interrupt
{
    Silent = 0,
    Toast = 1,
    Alert = 2,
    Takeover = 3
}

public enum ClassifiedBy
{
    /// <summary>A deterministic rule matched.</summary>
    Rule,

    /// <summary>
    /// No rule matched and the fallback was applied. These are the items the model classifies;
    /// SPEC.md lists only rule, model and user, but without this the model has no way to tell
    /// "a rule decided P3" from "nothing decided yet".
    /// </summary>
    Default,

    Model,
    User
}

public static class PriorityExtensions
{
    /// <summary>One step more urgent. P1 stays P1.</summary>
    public static Priority StepUp(this Priority p) => p == Priority.P1 ? Priority.P1 : p - 1;
}

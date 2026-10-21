namespace rNotice.Core.Items;

/// <summary>
/// The two axes plus who decided and why.
///
/// <see cref="Reason"/> is required. A priority the user can't interrogate is one they stop
/// trusting, so the constructor refuses an empty reason from rules as much as from the model.
/// </summary>
public sealed record Classification
{
    public Classification(Priority priority, Interrupt interrupt, ClassifiedBy by, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Every classification needs a one-line reason.", nameof(reason));

        Priority = priority;
        Interrupt = interrupt;
        By = by;
        Reason = reason;
    }

    public Priority Priority { get; init; }
    public Interrupt Interrupt { get; init; }
    public ClassifiedBy By { get; init; }
    public string Reason { get; init; }

    /// <summary>Rule output and the fallback can be replaced by a later poll. Model and user output can't.</summary>
    public bool IsSticky => By is ClassifiedBy.Model or ClassifiedBy.User;
}

using System;

namespace Homespool.Model.Entities;

/// <summary>
/// Somewhere a person has asked to be told things: one browser, and later one webhook or one topic.
/// </summary>
/// <remarks>
/// <para>
/// <b>One table for every kind, told apart by <see cref="Kind"/>.</b> What the kinds share - whose it
/// is, what it is called, whether delivering to it has been working - lives here; what a kind needs in
/// order to be delivered to lives on its own subclass, as typed columns. A JSON column of per-kind
/// settings was the alternative, and would have given up a unique index on a push endpoint and the
/// chance to encrypt one field of a webhook without the others.
/// </para>
/// <para>
/// <b>It belongs to a person, and the person's rights are not stored here.</b> Whether somebody should
/// hear about a printer is decided when there is something to say, from their team memberships at that
/// moment - so leaving a team or being deactivated stops notifications without anything here changing.
/// </para>
/// </remarks>
public abstract class NotificationDestination
{
    /// <summary>
    /// The longest <see cref="Name"/>. Enough to tell two browsers apart, short enough that it cannot be
    /// used to deface the page listing it.
    /// </summary>
    public const int NameMaxLength = 64;

    /// <summary>Sets the kind, which each subclass knows and nothing else may change.</summary>
    /// <param name="kind">The kind of destination this row is.</param>
    protected NotificationDestination(NotificationChannelKind kind)
    {
        Kind = kind;
    }

    public long Id { get; set; }

    /// <summary>
    /// The destination's public identifier - what the remove and test buttons carry, since <see cref="Id"/>
    /// counts up.
    /// </summary>
    public Guid Uuid { get; set; } = Guid.NewGuid();

    /// <summary>The account this destination belongs to. Cascades: nobody is left to tell.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// Which kind of destination this is, and so which subclass the row is. The discriminator.
    /// </summary>
    public NotificationChannelKind Kind { get; private set; }

    /// <summary>What the owner calls it, so they can tell which one to remove.</summary>
    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When something last arrived here, or null when nothing has yet.</summary>
    public DateTimeOffset? LastDeliveredAt { get; set; }

    /// <summary>When delivering here last failed, or null when it never has.</summary>
    public DateTimeOffset? LastFailedAt { get; set; }

    /// <summary>
    /// How many deliveries in a row have failed. Zero again after one succeeds.
    /// </summary>
    /// <remarks>
    /// Counted rather than acted on: a destination the delivery service has said is gone for good is
    /// deleted outright, and this is for everything short of that - so the settings page can say a
    /// browser has stopped hearing anything instead of the failure being silent.
    /// </remarks>
    public int ConsecutiveFailures { get; set; }
}

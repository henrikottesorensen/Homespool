using System;

namespace Homespool.Model.Entities;

/// <summary>
/// Settings that belong to this deployment and are chosen in the application rather than in its
/// configuration. Exactly one row.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the first of its kind here, and the bar for joining it is high.</b> Every other
/// deployment-wide setting lives in <c>.env</c> and reaches the application as options — which is
/// the right home for anything an operator sets once while standing up a stack, because it is
/// visible, greppable, and versioned wherever they keep that file. A row belongs here only when a
/// setting has to be <i>changed by somebody signed in</i>, and specifically when the act of turning
/// it on has to be answerable in the interface: a file cannot ask a question before it is saved.
/// </para>
/// <para>
/// <b>Not a key-value bag, deliberately.</b> Typed columns mean the schema says what exists, a
/// misspelling fails to compile rather than silently reading as absent, and nothing has to agree
/// about how a boolean is spelled. The cost is a migration per setting, which at this project's rate
/// of adding them is the cheaper side of that trade.
/// </para>
/// <para>
/// <b>One row, and the code depends on it.</b> The row is created on first read if it is not there,
/// so nothing has to seed it and a restored database that predates a column still works.
/// </para>
/// </remarks>
public class DeploymentSetting
{
    /// <summary>
    /// The identifier of the single row.
    /// </summary>
    /// <remarks>
    /// Fixed rather than generated, so "the settings" is a lookup by a known key instead of a query
    /// that has to decide what to do about a second row. A second row cannot be inserted without
    /// choosing this value again, which the primary key refuses.
    /// </remarks>
    public const int SingletonId = 1;

    /// <summary>
    /// Primary key, always <see cref="SingletonId"/>.
    /// </summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// Whether the camera stream server may contact a public STUN server to discover this
    /// deployment's own public address. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off is the decision, not the absence of one.</b> Left to itself go2rtc contacts a
    /// third-party STUN server unprompted and puts the resulting public address into every WebRTC
    /// offer it makes. For a project whose premise is that nothing about what you print leaves your
    /// network, reaching out to somebody else's server should be something a person chose.
    /// </para>
    /// <para>
    /// <b>What turning it on buys is watching from outside your own network</b> without naming a
    /// forwarded address by hand. LAN viewing does not need it and is unaffected either way, which
    /// is why the default costs nothing.
    /// </para>
    /// </remarks>
    public bool WebRtcStunEnabled { get; set; }

    /// <summary>
    /// When these settings were last changed.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

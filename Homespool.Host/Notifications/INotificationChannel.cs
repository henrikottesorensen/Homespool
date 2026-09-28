using System.Threading;
using System.Threading.Tasks;

using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.Notifications;

/// <summary>
/// Delivers a composed message to one kind of destination. The one thing a new kind of destination
/// has to implement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deciding who hears about what is not a channel's business</b>, and neither is composing the
/// words: both happen before a channel is asked, so a webhook and a browser can never disagree about
/// whether something was worth saying or how it was put.
/// </para>
/// <para>
/// <b>An implementation answers rather than throws</b> for anything its protocol can say - the caller
/// records the outcome on the destination, and a thrown exception would leave it recording nothing.
/// Cancellation is the exception: it is the caller's, and propagates.
/// </para>
/// </remarks>
public interface INotificationChannel
{
    /// <summary>The kind of destination this channel delivers to.</summary>
    NotificationChannelKind Kind { get; }

    /// <summary>
    /// Delivers <paramref name="message"/> to <paramref name="destination"/>, which is of
    /// <see cref="Kind"/>.
    /// </summary>
    /// <param name="destination">Where to deliver. Its concrete type is this channel's own.</param>
    /// <param name="message">What to say, already composed for the destination's owner.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    Task<DeliveryOutcome> DeliverAsync(NotificationDestination destination,
                                       NotificationMessage message,
                                       CancellationToken cancellationToken);
}

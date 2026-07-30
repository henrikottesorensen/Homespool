namespace Homespool.Host.Listeners;

/// <summary>
/// Endpoint metadata naming the only listener an endpoint may be served on.
/// </summary>
/// <param name="Listener">The listener this endpoint belongs to.</param>
public sealed record ListenerRequirement(ListenerClass Listener);

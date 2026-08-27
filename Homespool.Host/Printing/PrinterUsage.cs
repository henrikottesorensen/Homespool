using System;

namespace Homespool.Host.Printing;

/// <summary>
/// How much one person has used one printer lately.
/// </summary>
/// <remarks>
/// <b><see cref="LastStartedAt"/> earns its place as the tie-break.</b> Counts collide constantly on
/// a small rack - two printers on three jobs each - and without a second key the front page would
/// reorder itself on nothing but row order. Breaking the tie on recency also makes the ordering move
/// the way a person expects: the one you touched this morning comes first.
/// </remarks>
public sealed record PrinterUsage(int Jobs, DateTimeOffset LastStartedAt);

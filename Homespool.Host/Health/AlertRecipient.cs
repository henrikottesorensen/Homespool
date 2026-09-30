using System.Collections.Generic;

using Homespool.Model.Entities;

namespace Homespool.Host.Health;

/// <summary>One administrator a health alert goes to, where it can reach them, and in what language.</summary>
/// <param name="UserId">The administrator's account.</param>
/// <param name="Email">The administrator's address, or null when the account has none.</param>
/// <param name="Culture">A shipped culture the administrator chose, or null for the deployment default.</param>
/// <param name="Browsers">
/// Their browsers, as read with the list and detached from any context - none when they have turned
/// health notifications off.
/// </param>
public sealed record AlertRecipient(long UserId, string? Email, string? Culture, IReadOnlyList<WebPushDestination> Browsers);

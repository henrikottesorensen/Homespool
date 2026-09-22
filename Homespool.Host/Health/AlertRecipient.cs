namespace Homespool.Host.Health;

/// <summary>One address a health alert goes to, and the language to write it in.</summary>
/// <param name="Email">The administrator's address.</param>
/// <param name="Culture">A shipped culture the administrator chose, or null for the deployment default.</param>
public sealed record AlertRecipient(string Email, string? Culture);

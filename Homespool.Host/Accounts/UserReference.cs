using System;

namespace Homespool.Host.Accounts;

/// <summary>Who somebody is, as far as anyone else needs to know: a handle and a name.</summary>
/// <param name="Uuid">The account's public handle.</param>
/// <param name="UserName">The name they sign in with and are shown by.</param>
public sealed record UserReference(Guid Uuid, string UserName);

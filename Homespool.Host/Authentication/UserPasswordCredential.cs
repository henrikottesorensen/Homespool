namespace Homespool.Host.Authentication;

/// <summary>
/// What a page collected for the <see cref="Schemes.UserPassword"/> scheme: a username or address and
/// a password, presented to the scheme through <see cref="CredentialAuthentication"/>.
/// </summary>
/// <remarks>
/// The fields are nullable because a bound form may be missing one; the scheme refuses that rather
/// than the page having to. What the page owns is the binding itself - the form, its antiforgery
/// token and its validation - which is why the scheme never reads the form.
/// </remarks>
/// <param name="Login">The username or address, as typed.</param>
/// <param name="Password">The password, as typed.</param>
public sealed record UserPasswordCredential(string? Login, string? Password);

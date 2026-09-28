using Lib.Net.Http.WebPush.Authentication;

namespace Homespool.Host.Notifications.WebPush;

/// <summary>The deployment's VAPID key pair, as a browser needs one half and a request the other.</summary>
/// <param name="PublicKey">
/// The public key, base64url - handed to a browser as its <c>applicationServerKey</c> when it subscribes.
/// </param>
/// <param name="Authentication">Signs requests with the private half. Owned by <see cref="VapidKeyStore"/>.</param>
public sealed record VapidCredentials(string PublicKey, VapidAuthentication Authentication);

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Homespool.Host.IntegrationTest;

/// <summary>
/// Thin client over Mailpit's HTTP API (v1), shared by every test in this project that needs to
/// confirm a message actually arrived rather than trusting <c>SendEmailAsync</c>'s return value
/// alone.
/// </summary>
/// <remarks>
/// Response shapes checked directly against a running container (<c>curl</c>), not assumed from
/// documentation.
/// </remarks>
public sealed class MailpitClient : IDisposable
{
    private const string MailpitApiBaseUrl = "http://localhost:8025";

    private readonly HttpClient _http = new() { BaseAddress = new Uri(MailpitApiBaseUrl) };

    /// <summary>Empties the mailbox, so a previous test's message can't be mistaken for this one's.</summary>
    public async Task ClearAsync()
    {
        HttpResponseMessage response = await _http.DeleteAsync("/api/v1/messages");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Polls Mailpit's message list until the given recipient shows up, rather than assuming
    /// delivery is instantaneous the moment <c>SendEmailAsync</c> returns.
    /// </summary>
    public async Task<MailpitMessageSummary> AwaitMessageAsync(string recipientAddress)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            MailpitMessageList list = await _http.GetFromJsonAsync<MailpitMessageList>("/api/v1/messages")
                                       ?? throw new InvalidOperationException("Mailpit returned an empty response.");

            MailpitMessageSummary? match = list.Messages.FirstOrDefault(
                m => m.To.Any(a => string.Equals(a.Address, recipientAddress, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                return match;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            $"No message to {recipientAddress} appeared in Mailpit within 2 seconds. " +
            $"Is the container running ({MailpitApiBaseUrl})?");
    }

    public async Task<MailpitMessage> GetMessageAsync(string id) =>
        await _http.GetFromJsonAsync<MailpitMessage>($"/api/v1/message/{id}")
        ?? throw new InvalidOperationException("Mailpit returned an empty response.");

    public void Dispose() => _http.Dispose();

    // ---------- Mailpit API v1 shapes - only the fields these tests read ----------
    //
    // CA1812 flags all four as "never instantiated": true at compile time, since
    // System.Text.Json only ever constructs them through reflection during deserialization.
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json via reflection when deserializing Mailpit's API response.")]
    public sealed class MailpitMessageList
    {
        [JsonPropertyName("messages")]
        public MailpitMessageSummary[] Messages { get; set; } = [];
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json via reflection when deserializing Mailpit's API response.")]
    public sealed class MailpitMessageSummary
    {
        [JsonPropertyName("ID")]
        public string ID { get; set; } = string.Empty;

        [JsonPropertyName("To")]
        public MailpitAddress[] To { get; set; } = [];
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json via reflection when deserializing Mailpit's API response.")]
    public sealed class MailpitMessage
    {
        [JsonPropertyName("Subject")]
        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("To")]
        public MailpitAddress[] To { get; set; } = [];

        [JsonPropertyName("HTML")]
        public string HTML { get; set; } = string.Empty;
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Only ever constructed by System.Text.Json via reflection when deserializing Mailpit's API response.")]
    public sealed class MailpitAddress
    {
        [JsonPropertyName("Address")]
        public string Address { get; set; } = string.Empty;
    }
}

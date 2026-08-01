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
    /// How long to wait for a sent message to appear in Mailpit before calling it a failure.
    /// </summary>
    /// <remarks>
    /// Generous on purpose, and it costs nothing when things are healthy: the poll below returns on
    /// the first match, so this bound is only ever reached when the message genuinely is not coming.
    /// It was 2 seconds (20 attempts at 100 ms), which is ample for Mailpit's indexing on an idle
    /// machine and marginal on a busy one - a full rebuild running alongside is enough to lose the
    /// race. That produced exactly one unexplained failure on 2026-07-26 that never reproduced.
    /// <c>TelemetryWriterTests.FeedUntilAsync</c> exists for the same reason: a fixed budget that
    /// only passes on a warm machine is a flake waiting for a bad moment.
    /// </remarks>
    private static readonly TimeSpan MessageArrivalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polls Mailpit's message list until the given recipient shows up, rather than assuming
    /// delivery is instantaneous the moment <c>SendEmailAsync</c> returns.
    /// </summary>
    /// <param name="recipientAddress">The <c>To</c> address to wait for, matched case-insensitively.</param>
    /// <exception cref="TimeoutException">
    /// No such message arrived within <see cref="MessageArrivalTimeout"/>.
    /// </exception>
    public async Task<MailpitMessageSummary> AwaitMessageAsync(string recipientAddress)
    {
        // A deadline rather than an attempt count: what matters is how long the message has had to
        // arrive, not how many times we asked. An attempt count silently shortens the budget on a
        // machine slow enough for each request itself to take time - i.e. exactly when it is needed.
        DateTime deadline = DateTime.UtcNow + MessageArrivalTimeout;

        while (true)
        {
            MailpitMessageList list = await _http.GetFromJsonAsync<MailpitMessageList>("/api/v1/messages")
                                       ?? throw new InvalidOperationException("Mailpit returned an empty response.");

            MailpitMessageSummary? match = list.Messages.FirstOrDefault(
                m => m.To.Any(a => string.Equals(a.Address, recipientAddress, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"No message to {recipientAddress} appeared in Mailpit within {MessageArrivalTimeout.TotalSeconds:0} seconds. " +
                    $"Is the container running ({MailpitApiBaseUrl})?");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>
    /// Waits out <paramref name="window"/> and reports whether nothing addressed to
    /// <paramref name="recipientAddress"/> turned up - the counterpart to
    /// <see cref="AwaitMessageAsync"/>, for asserting that no mail was sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="AwaitMessageAsync"/> because the two budgets pull in opposite
    /// directions. A positive wait wants a ceiling high enough that a slow machine never hits it, and
    /// pays nothing when the message arrives. A negative one has to wait out its whole window on every
    /// run, since there is no event to observe - so it wants the shortest window that is still ample
    /// for delivery. Sharing one number makes the positive path fragile or the negative path slow.
    /// </para>
    /// <para>
    /// Returns a bool rather than throwing, so the caller asserts on the answer. Reaching for
    /// <see cref="AwaitMessageAsync"/> and expecting an exception looks equivalent and is not: any
    /// exception satisfies it, so a Mailpit container that is simply down makes the assertion pass
    /// while proving nothing. Errors talking to Mailpit still propagate from here.
    /// </para>
    /// </remarks>
    public async Task<bool> NoMessageArrivesAsync(string recipientAddress, TimeSpan window)
    {
        DateTime deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            MailpitMessageList list = await _http.GetFromJsonAsync<MailpitMessageList>("/api/v1/messages")
                                       ?? throw new InvalidOperationException("Mailpit returned an empty response.");

            if (list.Messages.Any(m => m.To.Any(a => string.Equals(a.Address, recipientAddress, StringComparison.OrdinalIgnoreCase))))
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return true;
    }

    public async Task<MailpitMessage> GetMessageAsync(string id)
    {
        return await _http.GetFromJsonAsync<MailpitMessage>($"/api/v1/message/{id}")
        ?? throw new InvalidOperationException("Mailpit returned an empty response.");
    }

    public void Dispose()
    {
        _http.Dispose();
    }

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

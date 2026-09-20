using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homespool.Host.Mail;

/// <summary>
/// Sends what <see cref="Enqueue"/> hands it on one background loop, so that the request that queued
/// a message does not wait for the mail server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Singleton with one reader</b>, the shape <c>TelemetryWriter</c> uses: registered as both the
/// <see cref="IDeferredEmailSender"/> everything injects and the hosted service that drains it, so
/// <see cref="Enqueue"/> only ever does a non-blocking channel write. Why the wait has to come off the
/// request at all is in <see cref="IDeferredEmailSender"/>'s remarks; this type is how.
/// </para>
/// <para>
/// <b><see cref="StartAsync"/> waits for the loop's own first statement</b>, the same signal
/// <c>TelemetryWriter</c> carries and for the same reason. Since .NET 10 the whole of
/// <see cref="ExecuteAsync"/> is scheduled onto the pool rather than run inline as far as its first
/// await, and <see cref="BackgroundService.StopAsync"/> cancels the stopping token - which cancels a
/// work item that has not started. A stop racing a busy pool therefore ends the loop
/// <em>before its first statement</em>, and the queue is abandoned whole: no send attempted,
/// <see cref="BackgroundService.ExecuteTask"/> quietly <see cref="TaskStatus.Canceled"/>, nothing in
/// the log to say so. This class was written without the signal and the suite found it within the
/// hour, which is the second time that change has cost this project a hunt.
/// </para>
/// <para>
/// <b>The scope is per message, not per loop.</b> <see cref="IEmailSender"/> is scoped, and a hosted
/// service outlives every scope, so one resolved at start-up would be a captive dependency held for
/// the life of the process. Each send gets its own.
/// </para>
/// <para>
/// <b>The queue drops the newest rather than the oldest when it is full.</b> Reaching
/// <see cref="Capacity"/> means sends are arriving faster than a mail server is accepting them, and
/// at that point a message that has been waiting is a promise already made where an arriving one is
/// not. Dropping is silent to the caller - the two forms this exists for cannot be told anything
/// without answering the question they refuse - but it is a warning in the log, with the recipient,
/// so an operator hearing "no reset mail arrived" has something to find.
/// </para>
/// <para>
/// <b>One bad message must not end the loop.</b> Nothing restarts it, so a throw that escaped would
/// mean every later reset and confirmation mail going unsent with nothing in the log to say why.
/// <see cref="IEmailSender"/> reports a failed send by return value rather than throwing, so both
/// catches here are last lines of defence rather than the routine path.
/// </para>
/// <para>
/// <b>Shutdown drains, briefly, and gives way to the telemetry flush.</b> <see cref="StopAsync"/>
/// completes the writer so the loop ends by itself once the queue is empty, and bounds the wait at
/// <see cref="MaxShutdownDrain"/> rather than letting it run to the host's shutdown timeout: that
/// timeout is sized for the telemetry buffers, hosted services stop one after another, and a mail
/// queue draining against an unreachable server must not spend a budget that exists to keep a print's
/// telemetry from being lost. Whatever is still queued when the budget runs out is counted and logged.
/// The ordering is not accidental - this service is registered before the telemetry writer, so it
/// stops after it, and the flush has taken what it needs before any of this runs.
/// </para>
/// </remarks>
public sealed class DeferredEmailSender : BackgroundService, IDeferredEmailSender
{
    /// <summary>
    /// How many messages may be waiting before further ones are dropped.
    /// </summary>
    /// <remarks>
    /// Sized well above what the callers can produce rather than tuned: both are bounded per target
    /// account by <c>AttemptLimiter</c> - five sends before a backoff that doubles from 30 s to an
    /// hour - and both sit behind the sign-in rate limit. Filling this means a mail server that has
    /// stopped accepting mail, which is the case the drop rule is written for, not a busy deployment.
    /// </remarks>
    private const int Capacity = 64;

    /// <summary>
    /// What the drain may spend after the writer is completed. See the class remarks for why it is
    /// bounded here rather than by the host's shutdown timeout.
    /// </summary>
    private static readonly TimeSpan MaxShutdownDrain = TimeSpan.FromSeconds(2);

    private readonly Channel<DeferredMail> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeferredEmailSender> _logger;
    private readonly TaskCompletionSource _loopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DeferredEmailSender(IServiceScopeFactory scopeFactory, ILogger<DeferredEmailSender> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _channel = Channel.CreateBounded<DeferredMail>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            },
            dropped => _logger.LogWarning(
                "The deferred mail queue is full at {Capacity} messages; the message to {Email} was dropped.",
                Capacity,
                dropped.Email));
    }

    /// <inheritdoc />
    public void Enqueue(string email, string subject, string htmlMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        // A full queue is not a failure here: the channel drops the message and reports it through
        // the callback above. A refused write means the writer is completed, which is shutdown.
        if (!_channel.Writer.TryWrite(new DeferredMail(email, subject, htmlMessage)))
        {
            _logger.LogWarning("The message to {Email} was not queued: the application is stopping.", email);
        }
    }

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken);

        // "Started" has to mean "running" - see the class remarks. WhenAny with the execute task so
        // that a loop which died before its first line lets start-up finish rather than hang.
        await Task.WhenAny(_loopEntered.Task, ExecuteTask ?? Task.CompletedTask);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(MaxShutdownDrain);

        // The budget bounds the wait, never a send already under way: nothing threads a token into
        // the loop, so what runs out here is this method's patience and not the SMTP conversation's.
        await base.StopAsync(budget.Token);

        int abandoned = _channel.Reader.Count;

        if (abandoned > 0)
        {
            _logger.LogWarning("Shutting down left {Count} queued messages unsent.", abandoned);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _loopEntered.TrySetResult();

        try
        {
            // Not the stopping token: the queue's own completion is what ends this, so a message
            // already taken off it is never abandoned halfway through its SMTP conversation.
            await foreach (DeferredMail mail in _channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                await SendAsync(mail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The deferred mail loop has stopped; queued messages will not be sent.");
        }
    }

    private async Task SendAsync(DeferredMail mail)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IEmailSender sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

            if (await sender.SendEmailAsync(mail.Email, mail.Subject, mail.HtmlMessage) == EmailSendResult.Failed)
            {
                // The sender has already logged why. This says which queued message it was, since by
                // now the request that asked for it is long answered.
                _logger.LogWarning("The queued message to {Email} was not sent.", mail.Email);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send the queued message to {Email}.", mail.Email);
        }
    }

    private sealed record DeferredMail(string Email, string Subject, string HtmlMessage);
}

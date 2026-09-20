namespace Homespool.Host.Mail;

/// <summary>
/// Accepts a message and returns without waiting for it to be sent, for the callers whose answer
/// must not take longer when there was something to send.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists for the two anonymous forms that mail a typed address</b> - the password reset and
/// the confirmation resend. Both answer identically whether or not the address belongs to an account,
/// so as not to say which addresses are registered. That sameness was written for what the response
/// <i>says</i>, and with SMTP configured the response also took a measurably different amount of time
/// to say it: a miss returns after one indexed lookup, where a hit first awaits a whole SMTP
/// conversation - connect, STARTTLS, authenticate, send, quit - which is a median 15 ms against a
/// mail server on loopback, and far more against one over a network. A caller who can time two
/// requests can read that difference, so the promise has to cover the work and not only the wording.
/// </para>
/// <para>
/// <b>Not a general replacement for <see cref="IEmailSender"/>, and deliberately narrow.</b> Every
/// other sender in the application has somebody to tell when a send fails, and telling them is the
/// right behaviour - silently directing someone to an inbox that will never receive anything is a bad
/// failure. These two are the exception: they cannot report a failure without reporting that there was
/// an account to fail for. Having already given up the result, they lose nothing by also giving up the
/// wait.
/// </para>
/// <para>
/// <b>A queued message can be dropped, and the caller is not told.</b> The queue is bounded and the
/// process may stop with messages still in it; both are logged where an operator will see them. That
/// is the same trade the two callers already make - neither can be told anything - and the cost of a
/// lost message is one more request from somebody who did not get their mail.
/// </para>
/// </remarks>
public interface IDeferredEmailSender
{
    /// <summary>
    /// Queues a message for sending, and returns as soon as it has been queued.
    /// </summary>
    void Enqueue(string email, string subject, string htmlMessage);
}

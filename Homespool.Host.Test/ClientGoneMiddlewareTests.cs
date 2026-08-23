using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Homespool.Host.Services;

namespace Homespool.Host.Test;

/// <summary>
/// <see cref="ClientGoneMiddleware"/> - a client that hung up is not a server fault.
/// </summary>
/// <remarks>
/// <b>The guard is what these are really about.</b> Turning a cancellation into 499 is one line; the
/// value is entirely in it applying to <em>this request's</em> abort and nothing else, because a
/// cancellation from anywhere else - a shutting-down host, a timeout somebody meant - is a real
/// failure that this would otherwise hide behind a status nobody reads as one.
/// </remarks>
public class ClientGoneMiddlewareTests
{
    /// <summary>The case it exists for: the browser went away mid-request.</summary>
    [Fact]
    public async Task AnswersThatTheClientWentAwayWith499()
    {
        // Arrange
        DefaultHttpContext context = new();
        using CancellationTokenSource aborted = new();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;

        // Act
        await new ClientGoneMiddleware().InvokeAsync(context, _ => throw new OperationCanceledException());

        // Assert
        context.Response.StatusCode.Should().Be(StatusCodes.Status499ClientClosedRequest);
    }

    /// <summary>
    /// <b>A cancellation the client did not cause still propagates.</b> The host shutting down throws
    /// the same exception type, and swallowing it would report a failed request as a tidy 499.
    /// </summary>
    [Fact]
    public async Task LetsACancellationTheClientDidNotCauseThrough()
    {
        // Arrange - the request itself is alive; something else cancelled.
        DefaultHttpContext context = new();

        // Act
        Func<Task> act = () =>
            new ClientGoneMiddleware().InvokeAsync(context, _ => throw new OperationCanceledException());

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>Everything else is somebody else's problem and passes straight through.</summary>
    [Fact]
    public async Task LetsAnyOtherFailureThrough()
    {
        DefaultHttpContext context = new();
        using CancellationTokenSource aborted = new();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;

        Func<Task> act = () =>
            new ClientGoneMiddleware().InvokeAsync(context, _ => throw new InvalidOperationException("real"));

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// A response already on the wire keeps the status it was sent with.
    /// </summary>
    /// <remarks>
    /// Not a nicety: writing a status after the headers have gone throws, so without the check this
    /// middleware would turn a cancelled streaming response into a second, different exception.
    /// </remarks>
    [Fact]
    public async Task LeavesAResponseThatHasAlreadyStarted()
    {
        // Arrange
        DefaultHttpContext context = new();
        using CancellationTokenSource aborted = new();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;
        context.Features.Set<IHttpResponseFeature>(new StartedResponse());

        // Act
        await new ClientGoneMiddleware().InvokeAsync(context, _ => throw new OperationCanceledException());

        // Assert
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK, "nothing may be rewritten once it is sent");
    }

    /// <summary>A response feature that claims it is already on the wire.</summary>
    private sealed class StartedResponse : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public System.IO.Stream Body { get; set; } = System.IO.Stream.Null;

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}

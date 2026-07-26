using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

using AwesomeAssertions;

namespace Homespool.FakePrinter.Test;

/// <summary>
/// The adversarial decorators - each models one misbehaviour a real printer refuses to produce
/// (<c>notes/fake-printer-harness.md</c>, "Cases a real printer refuses to produce").
/// </summary>
public class PolicyDecoratorTests
{
    private readonly PrinterIdentity _identity = PrinterIdentity.CreateRandom();
    private readonly FakeDevice _device = new();

    /// <summary>NoReply answers nothing at all - the response-timeout case.</summary>
    [Fact]
    public void NoReplyAnswersNothing()
    {
        NoReplyPolicy policy = new();

        policy.Answer(PauseCommand(1), _device).Should().BeEmpty();
    }

    /// <summary>DelayedReply postpones the inner policy's first reply by the configured amount.</summary>
    [Fact]
    public void DelayedReplyPostponesTheFirstReply()
    {
        DelayedReplyPolicy policy = new(new FirmwareFaithfulPolicy(_identity), TimeSpan.FromSeconds(12));
        _device.StartPrint(jobId: 1);

        IReadOnlyList<PlannedReply> replies = policy.Answer(PauseCommand(1), _device);

        replies.Should().HaveCount(1);
        replies[0].Delay.Should().Be(TimeSpan.FromSeconds(12));
    }

    /// <summary>WrongCommandId answers under a shifted id - the stray ack the server must ignore.</summary>
    [Fact]
    public void WrongCommandIdAnswersUnderAShiftedId()
    {
        WrongCommandIdPolicy policy = new(new FirmwareFaithfulPolicy(_identity));
        _device.StartPrint(jobId: 1);

        IReadOnlyList<PlannedReply> replies = policy.Answer(PauseCommand(41), _device);

        using JsonDocument reply = JsonDocument.Parse(replies[0].Payload!);
        reply.RootElement.GetProperty("command_id").GetUInt32().Should().Be(42, "the ack must not match the real command");
    }

    /// <summary>DoubleReply sends everything twice - two acks for one command.</summary>
    [Fact]
    public void DoubleReplySendsEveryReplyTwice()
    {
        DoubleReplyPolicy policy = new(new FirmwareFaithfulPolicy(_identity));
        _device.StartPrint(jobId: 1);

        IReadOnlyList<PlannedReply> replies = policy.Answer(PauseCommand(1), _device);

        replies.Should().HaveCount(2);
        replies[0].Payload.Should().Equal(replies[1].Payload);
    }

    /// <summary>RejectAll rejects with the configured reason regardless of device state.</summary>
    [Fact]
    public void RejectAllRejectsWithTheConfiguredReason()
    {
        RejectAllPolicy policy = new("Won't accept commands in error state");

        IReadOnlyList<PlannedReply> replies = policy.Answer(PauseCommand(1), _device);

        using JsonDocument reply = JsonDocument.Parse(replies[0].Payload!);
        reply.RootElement.GetProperty("event").GetString().Should().Be("REJECTED");
        reply.RootElement.GetProperty("reason").GetString().Should().Be("Won't accept commands in error state");
    }

    /// <summary>DisconnectOnCommand plans an abort with no payload - the mid-command death.</summary>
    [Fact]
    public void DisconnectOnCommandPlansAnAbort()
    {
        DisconnectOnCommandPolicy policy = new();

        IReadOnlyList<PlannedReply> replies = policy.Answer(PauseCommand(1), _device);

        replies.Should().HaveCount(1);
        replies[0].Payload.Should().BeNull();
        replies[0].DisconnectAfter.Should().BeTrue();
    }

    private static ServerCommandFrame PauseCommand(uint id)
    {
        return new ServerCommandFrame(ServerCommandKind.Json, id, Encoding.UTF8.GetBytes("""{"command": "PAUSE_PRINT"}"""));
    }
}

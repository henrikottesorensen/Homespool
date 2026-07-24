using System;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using NSubstitute;

using PrinterService.Host.PrusaConnect;

namespace PrinterService.Host.Test;

/// <summary>
/// <see cref="PrinterConnectionRegistry"/>, including the reconnect race a fast disconnect/reconnect
/// can trigger: a stale request's <c>finally</c>-block unregister must not delete a newer connection.
/// </summary>
public class PrinterConnectionRegistryTests
{
    /// <summary>An open connection that does nothing when written to - these tests only care about
    /// registry bookkeeping, never about what reaches the wire.</summary>
    private static IPrinterConnection OpenConnection()
    {
        IPrinterConnection connection = Substitute.For<IPrinterConnection>();
        connection.IsOpen.Returns(true);

        return connection;
    }

    [Fact]
    public void RegisterThenTryGetReturnsTheSameConnection()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnection connection = OpenConnection();

        // Act
        registry.Register(1, connection);

        // Assert
        registry.TryGet(1, out IPrinterConnection? found).Should().BeTrue();
        found.Should().BeSameAs(connection);
    }

    [Fact]
    public void IsConnectedReflectsTheConnectionsIsOpenState()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnection connection = Substitute.For<IPrinterConnection>();
        connection.IsOpen.Returns(false);
        registry.Register(1, connection);

        // Act + Assert
        registry.IsConnected(1).Should().BeFalse();

        connection.IsOpen.Returns(true);
        registry.IsConnected(1).Should().BeTrue();
    }

    [Fact]
    public void UnregisteringAStaleConnectionDoesNotRemoveANewerOneForTheSamePrinter()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnection connectionA = OpenConnection();
        IPrinterConnection connectionB = OpenConnection();

        registry.Register(1, connectionA);
        // Simulates a fast reconnect: a new connection registers for the same printer before the
        // stale request's finally block runs its unregister.
        registry.Register(1, connectionB);

        // Act
        registry.Unregister(1, connectionA);

        // Assert
        registry.TryGet(1, out IPrinterConnection? found).Should().BeTrue();
        found.Should().BeSameAs(connectionB);
    }

    [Fact]
    public void UnregisteringTheCurrentConnectionRemovesIt()
    {
        // Arrange
        PrinterConnectionRegistry registry = new();
        IPrinterConnection connection = OpenConnection();
        registry.Register(1, connection);

        // Act
        registry.Unregister(1, connection);

        // Assert
        registry.TryGet(1, out _).Should().BeFalse();
        registry.IsConnected(1).Should().BeFalse();
    }
}

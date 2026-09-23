namespace Homespool.Data;

/// <summary>
/// Where this process keeps telemetry, as decided when its services were registered.
/// </summary>
/// <remarks>
/// <b>The mode in force, not the one saved.</b> <see cref="StorageOptions.TelemetryInMemory"/> is read
/// once, at registration, to pick the database <see cref="TelemetryDbContext"/> opens; changing the
/// setting afterwards changes configuration and options but not that choice, until a restart. Anything
/// that has to say what a restart would cost asks this rather than the options, which may already
/// describe the next process.
/// </remarks>
/// <param name="InMemory">Whether samples and events are discarded when this process stops.</param>
public sealed record TelemetryStorageMode(bool InMemory);

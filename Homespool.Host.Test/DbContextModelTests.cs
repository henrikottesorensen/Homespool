using System;
using System.Collections.Generic;
using System.Linq;

using AwesomeAssertions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Data;

namespace Homespool.Host.Test;

/// <summary>
/// Both database models build without a warning from EF.
/// </summary>
/// <remarks>
/// <para>
/// <b>EF validates a model once per process</b>, when the first context with a given configuration
/// builds it, and tells only that context's logger what it found. A test capturing the logs of a
/// startup therefore sees a model warning only when it happens to build the model first: alone it
/// fails, in a full run it usually passes, and which test fails is no clue to what is wrong.
/// </para>
/// <para>
/// <b>So each test here builds a model of its own.</b> With service-provider caching off, EF builds
/// and validates the model afresh for this context, whatever the process has built before, and the
/// logger given here is the one that hears it.
/// </para>
/// <para>
/// <b>A model warning is worth failing on.</b> The one that prompted this was a column default EF
/// would have written in place of an explicit <c>Undefined</c>, which is a defect rather than noise.
/// </para>
/// </remarks>
public class DbContextModelTests
{
    [Fact]
    public void TheApplicationModelBuildsWithoutWarnings()
    {
        // Act
        ModelBuild build = BuildModel<HomespoolDbContext>(options => new(options));

        // Assert
        build.ModelRecords.Should().NotBeEmpty("a model built out of this logger's sight passes for the wrong reason");
        build.Warnings.Should().BeEmpty("EF warns about a model where it will do something the configuration never said");
    }

    [Fact]
    public void TheTelemetryModelBuildsWithoutWarnings()
    {
        // Act
        ModelBuild build = BuildModel<TelemetryDbContext>(options => new(options));

        // Assert
        build.ModelRecords.Should().NotBeEmpty("a model built out of this logger's sight passes for the wrong reason");
        build.Warnings.Should().BeEmpty("EF warns about a model where it will do something the configuration never said");
    }

    /// <summary>
    /// Builds <typeparamref name="TContext"/>'s model afresh on SQLite, and returns what EF logged about
    /// the model while doing it.
    /// </summary>
    private static ModelBuild BuildModel<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        FakeLogCollector logs = new();

        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug)
                                                                                  .AddProvider(new FakeLoggerProvider(logs)));

        DbContextOptions<TContext> options = new DbContextOptionsBuilder<TContext>()
                                             .UseSqlite("Data Source=:memory:")
                                             .UseLoggerFactory(loggerFactory)
                                             .EnableServiceProviderCaching(false)
                                             .Options;

        using (TContext context = create(options))
        {
            _ = context.Model;
        }

        IReadOnlyList<FakeLogRecord> records = logs.GetSnapshot();

        return new ModelBuild(records.Where(record => record.Category?.StartsWith("Microsoft.EntityFrameworkCore.Model", StringComparison.Ordinal) == true)
                                     .ToList(),
                              records.Where(record => record.Level >= LogLevel.Warning)
                                     .Select(record => $"{record.Id.Name ?? record.Category}: {record.Message}")
                                     .ToList());
    }

    /// <summary>What a model build logged.</summary>
    /// <param name="ModelRecords">EF's records about the model at any level, which show the build happened in sight.</param>
    /// <param name="Warnings">Every warning or worse, from any category.</param>
    private sealed record ModelBuild(List<FakeLogRecord> ModelRecords, List<string> Warnings);
}

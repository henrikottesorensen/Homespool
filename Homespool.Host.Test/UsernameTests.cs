using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.PrusaConnect.DTO.App;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// The username - what someone signs in with, and what the interface calls them.
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>DisplayNameTests</c>, which covered the display-only half of "the username should not
/// be the email address". That half is gone: the account's own <c>UserName</c> now carries the name,
/// so there is one name rather than two and nothing left to seed from an address.
/// </para>
/// <para>
/// The rules are asserted through a real <see cref="UserManager{TUser}"/> over a migrated database,
/// because they live in two places that only meet there: <see cref="UsernameValidator"/> decides
/// what is accepted, and <see cref="SkeletonLookupNormalizer"/> decides what is the same name - and
/// the second is enforced by the unique index, not by any code of ours.
/// </para>
/// </remarks>
public sealed class UsernameTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-username-{Guid.NewGuid():N}.db");

    private int _addresses;

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The letters people are actually called by, in whichever alphabet: Danish, Icelandic, German,
    /// Turkish, Vietnamese, Polish, Russian, Greek, Japanese - plus the three punctuation marks.
    /// </summary>
    [Theory]
    [InlineData("henrik")]
    [InlineData("søren")]
    [InlineData("Ægir")]
    [InlineData("Þórður")]
    [InlineData("Müller")]
    [InlineData("yıldız")]
    [InlineData("Nguyễn")]
    [InlineData("Łukasz")]
    [InlineData("Иван")]
    [InlineData("Νίκος")]
    [InlineData("田中")]
    [InlineData("henrik.sorensen")]
    [InlineData("a-b_c7")]
    public async Task AUsernameMayBeAnyNameInOneAlphabet(string name)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        IdentityResult result = await users.CreateAsync(NewUser(name));

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    /// <summary>
    /// No <c>@</c>, so a username can never be shaped like an address - which is what makes
    /// <c>LoginModel</c>'s username-then-email resolution unambiguous rather than merely ordered.
    /// The rest is the identifier profile: no whitespace, no invisible characters, no compatibility
    /// digraphs - and no decomposed accents, because the entry points normalise and a validator
    /// that met one would mean an entry point had not.
    /// </summary>
    [Theory]
    [InlineData("henrik@example.com", "an address")]
    [InlineData("henrik+rig", "an address tag")]
    [InlineData("hen rik", "whitespace")]
    [InlineData("hen\u200Drik", "a zero-width joiner")]
    [InlineData("ǆeto", "a compatibility digraph")]
    [InlineData("e\u0301mile", "a decomposed accent - not the form the entry points store")]
    public async Task AUsernameMayNotBeShapedLikeAnAddressOrCarryWhatTheProfileRefuses(string name, string because)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        IdentityResult result = await users.CreateAsync(NewUser(name));

        result.Succeeded.Should().BeFalse(because);
        result.Errors.Should().Contain(e => e.Code == "InvalidUserName", because);
    }

    /// <summary>
    /// One alphabet per name: a Cyrillic letter among Latin ones is the cross-script homoglyph, and
    /// digits from two number systems are the same trick with numbers.
    /// </summary>
    [Theory]
    [InlineData("hеnrik", "a Cyrillic е among Latin letters")]
    [InlineData("Toys-Я-Us", "Latin and Cyrillic")]
    [InlineData("a1١", "ASCII and Arabic-Indic digits")]
    public async Task AUsernameMayNotMixAlphabets(string name, string because)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        IdentityResult result = await users.CreateAsync(NewUser(name));

        result.Succeeded.Should().BeFalse(because);
        result.Errors.Should().Contain(e => e.Code == "UserNameMixesScripts", because);
    }

    /// <summary>
    /// Two names that look alike are one name: the second registration is a duplicate, refused by
    /// the unique index on the skeleton key rather than by any letter being forbidden. The pairs are
    /// the ones the flat character set used to have to exclude - or could not, in ASCII's case.
    /// </summary>
    [Theory]
    [InlineData("modern", "rnodern", "rn reads as m, in plain ASCII")]
    [InlineData("por", "þor", "thorn reads as p")]
    [InlineData("AEgir", "Ægir", "the ligature reads as AE")]
    [InlineData("henrik", "Henrik", "case")]
    [InlineData("Ivan", "lvan", "capital I reads as l")]
    public async Task ALookalikeIsTheSameNameAndSoADuplicate(string first, string second, string because)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        (await users.CreateAsync(NewUser(first))).Succeeded.Should().BeTrue();

        IdentityResult result = await users.CreateAsync(NewUser(second));

        result.Succeeded.Should().BeFalse(because);
        result.Errors.Should().Contain(e => e.Code == "DuplicateUserName", because);
    }

    /// <summary>
    /// The other half of "the same name": whichever lookalike is typed at sign-in resolves to the one
    /// account, so a name that looks like yours is yours.
    /// </summary>
    [Fact]
    public async Task ALookalikeResolvesToTheOneAccount()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        HSUser created = NewUser("por");
        (await users.CreateAsync(created)).Succeeded.Should().BeTrue();

        HSUser? found = await users.FindByNameAsync("þor");

        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Id);
    }

    /// <summary>The key is the skeleton of the NFKC form, upper-cased; an address keeps Identity's key.</summary>
    [Fact]
    public void TheLookupKeyIsTheUpperCasedSkeleton()
    {
        SkeletonLookupNormalizer normaliser = new();

        normaliser.NormalizeName("hеnrik").Should().Be(normaliser.NormalizeName("henrik"), "a Cyrillic е is the lookalike");
        normaliser.NormalizeName("ﬁle").Should().Be("FILE", "NFKC folds the ligature first");
        normaliser.NormalizeName("Ivan").Should().Be(normaliser.NormalizeName("lvan"));
        normaliser.NormalizeName(null).Should().BeNull();
        normaliser.NormalizeEmail("Rig@Example.com").Should().Be("RIG@EXAMPLE.COM");
    }

    /// <summary>
    /// A row written before the skeleton became the key, or by an older Unicode table, gets the
    /// current key at start-up - otherwise the account can no longer be found by name.
    /// </summary>
    [Fact]
    public async Task StartUpRefreshesAStaleLookupKey()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser ivan = NewUser("Ivan");
        ivan.NormalizedUserName = "IVAN";
        ivan.NormalizedEmail = ivan.Email!.ToUpperInvariant();
        context.Users.Add(ivan);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (IServiceProvider provider, FakeLogCollector _) = RefreshServices(context);

        int rewritten = await UsernameKeyRefresh.RefreshAsync(provider, TestContext.Current.CancellationToken);

        rewritten.Should().Be(1);
        ivan.NormalizedUserName.Should().Be(new SkeletonLookupNormalizer().NormalizeName("Ivan"));
    }

    /// <summary>
    /// Two existing accounts that would share a key are left as they are and named in an error,
    /// rather than the service refusing to start on the unique index. Every other row is still done.
    /// </summary>
    [Fact]
    public async Task StartUpReportsACollisionAndRefreshesTheRest()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        HSUser modern = NewUser("modern");
        modern.NormalizedUserName = "MODERN";
        HSUser lookalike = NewUser("rnodern");
        lookalike.NormalizedUserName = "RNODERN";
        HSUser ivan = NewUser("Ivan");
        ivan.NormalizedUserName = "IVAN";

        foreach (HSUser user in new[] { modern, lookalike, ivan })
        {
            user.NormalizedEmail = user.Email!.ToUpperInvariant();
            context.Users.Add(user);
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        (IServiceProvider provider, FakeLogCollector logs) = RefreshServices(context);

        int rewritten = await UsernameKeyRefresh.RefreshAsync(provider, TestContext.Current.CancellationToken);

        rewritten.Should().Be(1, "only Ivan's key was stale and safe to rewrite");
        modern.NormalizedUserName.Should().Be("MODERN");
        lookalike.NormalizedUserName.Should().Be("RNODERN");

        FakeLogRecord error = logs.GetSnapshot().Should().ContainSingle(r => r.Level == LogLevel.Error).Subject;
        error.Message.Should().Contain("'modern'").And.Contain("'rnodern'");
    }

    /// <summary>
    /// The app API's <c>Name</c> is the username. An API called <c>Name</c> should not hand out an
    /// address, and it no longer has to: the account has a name of its own.
    /// </summary>
    [Fact]
    public void TheAppApiReturnsTheUsername()
    {
        HSUser user = new() { UserName = "henrik", Email = "rig@example.com" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("henrik");
    }

    /// <summary>
    /// The fallback exists only because <c>UserName</c> is nullable on the base type. Nothing this
    /// application creates gets there - but a blank name would render as a blank greeting, and the
    /// address is the one other identifier always present.
    /// </summary>
    [Fact]
    public void TheAppApiFallsBackToTheEmailWhenThereIsNoUsername()
    {
        HSUser user = new() { Email = "rig@example.com" };

        UserReadDTO.FromEntity(user, []).Name.Should().Be("rig@example.com");
    }

    private static UserManager<HSUser> Users(HomespoolDbContext context)
    {
        return IdentityTestHarness.BuildIdentityServices(context).users;
    }

    private static (IServiceProvider provider, FakeLogCollector logs) RefreshServices(HomespoolDbContext context)
    {
        ServiceCollection services = new();
        services.AddFakeLogging();
        services.AddSingleton(context);
        services.AddScoped<ILookupNormalizer, SkeletonLookupNormalizer>();

        ServiceProvider provider = services.BuildServiceProvider();

        return (provider, provider.GetRequiredService<FakeLogCollector>());
    }

    private HSUser NewUser(string name)
    {
        return new HSUser(name) { Email = $"user{++_addresses}@example.com" };
    }

    private async Task<HomespoolDbContext> MigratedContextAsync()
    {
        DbContextOptions<HomespoolDbContext> options = new DbContextOptionsBuilder<HomespoolDbContext>()
                                                       .UseSqlite($"Data Source={_databasePath}")
                                                       .Options;

        HomespoolDbContext context = new(options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
    }
}

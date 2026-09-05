using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
/// because <see cref="UsernameValidator"/> judges a new name against every existing one, and only
/// the real thing has existing ones.
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
    /// forms - and no decomposed accents, because the entry points store the clean form and a
    /// validator that met one would mean an entry point had not.
    /// </summary>
    [Theory]
    [InlineData("henrik@example.com", "an address")]
    [InlineData("henrik+rig", "an address tag")]
    [InlineData("hen rik", "whitespace")]
    [InlineData("hen\u200Drik", "a zero-width joiner")]
    [InlineData("ǆeto", "a compatibility digraph")]
    [InlineData("ﬁle", "a ligature is refused, not folded to the letters it looks like")]
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
    /// A name in another alphabet that reads as an existing one is the impersonation shape, and is
    /// refused as looking like a name somebody already has - whichever case it is typed in.
    /// </summary>
    [Theory]
    [InlineData("scope", "ѕсоре", "five Cyrillic letters spelling a Latin name")]
    [InlineData("Scope", "ѕсоре", "case does not hide it, since sign-in ignores case")]
    public async Task ACrossScriptLookalikeOfAnExistingNameIsRefused(string existing, string lookalike, string because)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        (await users.CreateAsync(NewUser(existing))).Succeeded.Should().BeTrue();

        IdentityResult result = await users.CreateAsync(NewUser(lookalike));

        result.Succeeded.Should().BeFalse(because);
        result.Errors.Should().Contain(e => e.Code == "UserNameLooksLikeAnother", because);
    }

    /// <summary>
    /// A same-alphabet lookalike is what ASCII has always allowed, and it stays allowed: two real
    /// people can be Ian and Lan, or Erna and Ema. Merging these was tried once and taken out.
    /// </summary>
    [Theory]
    [InlineData("modern", "rnodern", "rn reads as m")]
    [InlineData("por", "þor", "thorn reads as p")]
    [InlineData("AEgir", "Ægir", "the ligature reads as AE")]
    [InlineData("Ian", "Lan", "capital I reads as l")]
    [InlineData("Erna", "Ema", "rn reads as m, in real names")]
    public async Task ASameScriptLookalikeIsADifferentName(string first, string second, string because)
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        (await users.CreateAsync(NewUser(first))).Succeeded.Should().BeTrue();

        IdentityResult result = await users.CreateAsync(NewUser(second));

        result.Succeeded.Should().BeTrue(because + ": " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }

    /// <summary>The exact duplicate, case aside, is still Identity's own refusal.</summary>
    [Fact]
    public async Task TheSameNameInAnotherCaseIsADuplicate()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        (await users.CreateAsync(NewUser("henrik"))).Succeeded.Should().BeTrue();

        IdentityResult result = await users.CreateAsync(NewUser("Henrik"));

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == "DuplicateUserName");
    }

    /// <summary>A rename to a lookalike of one's own name is not a collision with oneself.</summary>
    [Fact]
    public async Task RenamingToALookalikeOfYourOwnNameIsAllowed()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = Users(context);

        HSUser user = NewUser("scope");
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();

        IdentityResult result = await users.SetUserNameAsync(user, "ѕсоре");

        result.Succeeded.Should().BeTrue("an all-Cyrillic name is one alphabet, and the only name it resembles is this account's own");
    }

    /// <summary>
    /// What the entry points hand to Identity: an acceptable name in its clean form, so a decomposed
    /// accent is composed - and an unacceptable one exactly as typed, so the validator can say why.
    /// </summary>
    [Fact]
    public void AnEntryPointStoresTheCleanFormOfAnAcceptableNameAndLeavesTheRestAlone()
    {
        Usernames.Prepare("e\u0301mile").Should().Be("émile", "the decomposed accent is composed");
        Usernames.Prepare("ﬁle").Should().Be("ﬁle", "a ligature is a finding, not something to fold away");
        Usernames.Prepare("hеnrik").Should().Be("hеnrik", "a mixed-script name is left for the validator to refuse");
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

using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// An address this server keeps is one a person can read: the rule, the two places that enforce it
/// whatever page is asking, and the three forms that say it beside the field.
/// </summary>
/// <remarks>
/// The framework's <c>[EmailAddress]</c> checks a shape and refuses a line break, and lets through
/// everything below. A kept address is shown - to an administrator, in a mail header, beside an
/// invitation - so this is about what is stored, not about a log. The escapes are written rather than
/// pasted, as in <c>LogTextTests</c>.
/// </remarks>
public sealed class EmailAddressTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-email-{Guid.NewGuid():N}.db");

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

    // ---- the rule ----
    [Theory]
    [InlineData("anna\u001B[2J@example.net")]
    [InlineData("anna\u202E@example.net")]
    [InlineData("anna\u200B@example.net")]
    [InlineData("anna\u0000@example.net")]
    [InlineData("anna\u0009@example.net")]
    [InlineData("anna@example\u2028.net")]
    public void AnAddressHoldingAnUnprintableCharacterIsNotKept(string address)
    {
        new EmailAddressAttribute().IsValid(address).Should().BeTrue("the framework's rule lets it through, which is why this one exists");

        EmailAddresses.IsStorable(address).Should().BeFalse();
    }

    /// <summary>A character that takes two <see cref="char"/>s, and half of one.</summary>
    [Fact]
    public void ACharacterIsJudgedWhole()
    {
        EmailAddresses.IsStorable("anna" + char.ConvertFromUtf32(0xE0041) + "@example.net").Should().BeFalse();
        EmailAddresses.IsStorable("anna" + char.ConvertFromUtf32(0x1F600)[0] + "@example.net").Should().BeFalse();
    }

    [Fact]
    public void AnAddressIsKeptAtTheLongestAMailServerTakesAndNotPastIt()
    {
        string longest = new string('x', EmailAddresses.MaxLength - "@example.net".Length) + "@example.net";

        EmailAddresses.IsStorable(longest).Should().BeTrue();
        EmailAddresses.IsStorable("x" + longest).Should().BeFalse();
    }

    [Theory]
    [InlineData("anna@example.net")]
    [InlineData("anna.s\u00F8rensen+printers@example.net")]
    [InlineData("anna@b\u00FCcher.example")]
    [InlineData("root@localhost")]
    public void AnOrdinaryAddressIsKept(string address)
    {
        EmailAddresses.IsStorable(address).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoAddressIsNotOneToKeep(string? address)
    {
        EmailAddresses.IsStorable(address).Should().BeFalse();
    }

    // ---- on an account, whichever page asks ----
    [Fact]
    public async Task AnAccountIsNotCreatedWithAnAddressThatIsNotKept()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = IdentityTestHarness.BuildIdentityServices(context).users;

        IdentityResult result = await users.CreateAsync(new HSUser("anna") { Email = "anna\u202E@example.net" });

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Code == EmailAddressValidator.ErrorCode);
        (await context.Users.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    /// <summary>
    /// The confirmed change of address is the other way one reaches an account, and it arrives from a
    /// link rather than a form - so only the account's own validator stands in front of it.
    /// </summary>
    [Fact]
    public async Task AnAccountsAddressIsNotChangedToOneThatIsNotKept()
    {
        await using HomespoolDbContext context = await MigratedContextAsync();
        UserManager<HSUser> users = IdentityTestHarness.BuildIdentityServices(context).users;
        HSUser user = new("anna") { Email = "anna@example.net" };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();

        const string dirty = "anna\u200B@example.net";
        string token = await users.GenerateChangeEmailTokenAsync(user, dirty);

        IdentityResult result = await users.ChangeEmailAsync(user, dirty, token);

        result.Succeeded.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Code == EmailAddressValidator.ErrorCode);
        context.ChangeTracker.Clear();
        (await context.Users.SingleAsync(TestContext.Current.CancellationToken)).Email.Should().Be("anna@example.net");
    }

    // ---- beside the field ----
    [Fact]
    public void TheAttributeRefusesWhatTheRuleRefusesAndLeavesAnEmptyFieldToRequired()
    {
        StorableEmailAddressAttribute attribute = new();

        attribute.IsValid("anna\u202E@example.net").Should().BeFalse();
        attribute.IsValid("anna@example.net").Should().BeTrue();
        attribute.IsValid(null).Should().BeTrue();
        attribute.IsValid(string.Empty).Should().BeTrue();
        attribute.ErrorMessage.Should().Be(StorableEmailAddressAttribute.MessageKey, "the key is what the page's localiser looks up");
    }

    /// <summary>
    /// The three fields where an address is typed to be kept. A field that only looks one up does not
    /// carry it: a dirty address finds no account, and refusing it there would only say so.
    /// </summary>
    [Theory]
    [InlineData(typeof(Pages.SetupModel.InputModel), nameof(Pages.SetupModel.InputModel.Email))]
    [InlineData(typeof(Pages.Admin.Invites.CreateModel.InputModel), nameof(Pages.Admin.Invites.CreateModel.InputModel.Email))]
    [InlineData(typeof(Pages.Account.Manage.EmailModel.InputModel), nameof(Pages.Account.Manage.EmailModel.InputModel.NewEmail))]
    public void AFieldWhereAnAddressIsTypedToBeKeptSaysSo(Type inputModel, string property)
    {
        PropertyInfo field = inputModel.GetProperty(property)!;

        field.GetCustomAttribute<StorableEmailAddressAttribute>().Should().NotBeNull();
        field.GetCustomAttribute<EmailAddressAttribute>().Should().NotBeNull("the shape is still the framework's to check");
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AwesomeAssertions;

using Homespool.Host.Configuration;
using Homespool.Host.Mail;

namespace Homespool.Host.Test;

/// <summary>
/// The allowlist of settings an administrator may change is well formed, and keeps saying what it
/// says after the options classes are edited.
/// </summary>
/// <remarks>
/// <para>
/// <b>What these catch is a rename.</b> The list names a property with <c>nameof</c>, so a rename
/// carries the entry with it - but the <i>section</i> is a separate string, and the path written into
/// the settings file is built from both. An entry naming a section its options class does not use
/// would write a key nothing binds: saved, reloaded, and silently ignored.
/// </para>
/// <para>
/// <b>What they deliberately do not check is whether a grade is true.</b> That is a claim about how a
/// consumer reads its options, and no reflection over this list can see it. The check that bites is
/// the one added alongside the monitor transition - that no consumer of a live-graded class captures
/// <c>IOptions.Value</c> - and, for each live setting, a test that writes the file and watches the
/// behaviour change without a restart.
/// </para>
/// </remarks>
public class EditableSettingsTests
{
    public static TheoryData<string> AllPaths()
    {
        TheoryData<string> data = [];

        foreach (EditableSetting setting in EditableSettings.All)
        {
            data.Add(setting.Path);
        }

        return data;
    }

    [Fact]
    public void TheListIsNotEmpty()
    {
        EditableSettings.All.Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public void EveryEntryNamesARealProperty(string path)
    {
        EditableSetting setting = Find(path);

        PropertyInfo? property = setting.OptionsType.GetProperty(
            setting.Key,
            BindingFlags.Public | BindingFlags.Instance);

        property.Should().NotBeNull(
            "{0} names a property that does not exist on {1}", path, setting.OptionsType.Name);

        property!.CanWrite.Should().BeTrue(
            "{0} is editable, so binding has to be able to set it", path);
    }

    [Theory]
    [MemberData(nameof(AllPaths))]
    public void EveryEntrySectionMatchesItsOptionsClass(string path)
    {
        EditableSetting setting = Find(path);

        FieldInfo? sectionName = setting.OptionsType.GetField(
            "SectionName",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

        sectionName.Should().NotBeNull(
            "{0} should declare a SectionName constant", setting.OptionsType.Name);

        sectionName!.GetRawConstantValue().Should().Be(
            setting.Section,
            "the path written to the settings file has to be the one configuration binds");
    }

    [Fact]
    public void NoSettingIsListedTwice()
    {
        IEnumerable<string> duplicated = EditableSettings.All
                                                         .GroupBy(setting => setting.Path)
                                                         .Where(group => group.Count() > 1)
                                                         .Select(group => group.Key);

        duplicated.Should().BeEmpty();
    }

    [Fact]
    public void NoSettingIsUngraded()
    {
        EditableSettings.All.Should().NotContain(setting => setting.Grade == SettingGrade.Undefined);
    }

    [Fact]
    public void OnlyDeferredSettingsNameTheMomentTheyApply()
    {
        foreach (EditableSetting setting in EditableSettings.All)
        {
            if (setting.Grade == SettingGrade.Deferred)
            {
                setting.AppliesWhenKey.Should().NotBeNullOrWhiteSpace(
                    "{0} is deferred, so the page has to be able to say what it defers to", setting.Path);
            }
            else
            {
                setting.AppliesWhenKey.Should().BeNull(
                    "{0} is {1}, whose moment the grade already names", setting.Path, setting.Grade);
            }
        }
    }

    /// <summary>
    /// The SMTP password is the only credential on the list, and it is the reason the settings file
    /// stores ciphertext and is written 0600. A second secret arriving without that being deliberate
    /// is what this catches.
    /// </summary>
    [Fact]
    public void ThePasswordIsTheOnlySecret()
    {
        EditableSettings.All
                        .Where(setting => setting.IsSecret)
                        .Select(setting => setting.Path)
                        .Should()
                        .BeEquivalentTo([$"{SmtpOptions.SectionName}:{nameof(SmtpOptions.Password)}"]);
    }

    /// <summary>
    /// Every mail setting is restart-graded, because three startup decisions read this section:
    /// which email sender is registered, whether the alert service runs, and whether new accounts are
    /// confirmed at creation. One of them turning live by accident is a behaviour change nobody asked
    /// for.
    /// </summary>
    [Fact]
    public void EveryMailSettingRequiresARestart()
    {
        EditableSettings.All
                        .Where(setting => setting.Section == SmtpOptions.SectionName)
                        .Should()
                        .OnlyContain(setting => setting.Grade == SettingGrade.Restart);
    }

    [Fact]
    public void PathsMatchTheList()
    {
        EditableSettings.Paths.Should().BeEquivalentTo(EditableSettings.All.Select(setting => setting.Path));
    }

    [Fact]
    public void FindAnswersNullForAKeyThatIsNotEditable()
    {
        EditableSettings.Find("Listeners:UserPort").Should().BeNull();
    }

    private static EditableSetting Find(string path)
    {
        EditableSetting? setting = EditableSettings.Find(path);

        setting.Should().NotBeNull();

        return setting!;
    }
}

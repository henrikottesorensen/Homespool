using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.Options;

using NSubstitute;

using Homespool.Host.Cameras;
using Homespool.Host.Certificates;
using Homespool.Host.Configuration;

namespace Homespool.Host.Test;

/// <summary>
/// A setting graded as taking effect without a restart has to be read somewhere that can see a
/// change. This is the part of that claim a machine can check.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> <c>IOptions&lt;T&gt;</c> is resolved once and never changes, so a consumer
/// that takes one can never obey an edit - and nothing about that is visible at the call site or in a
/// passing test. The grade would simply become untrue, and the page would go on promising it.
/// </para>
/// <para>
/// <b>The rule is per options class, not per property</b>, because a constructor parameter is all
/// reflection can see. So a class carrying even one live or deferred setting puts every consumer on a
/// monitor or a snapshot, including consumers that only ever read a fixed value from it. That is
/// deliberate collateral: it costs those consumers nothing, and it means somebody adding a read of a
/// live property later cannot get it wrong.
/// </para>
/// <para>
/// <b>Two blind spots, both real.</b> A consumer that resolves options from the service provider
/// rather than taking them as a parameter is invisible here - <c>AddHomespoolData</c> does that, to
/// read a value fixed at startup, which is why it is left alone. And: A consumer may still take a monitor and
/// capture <c>CurrentValue</c> in its constructor, which is a snapshot wearing the right type -
/// <c>TelemetryWriter</c> does exactly that on purpose, for the two values graded as needing a
/// restart. Only a behavioural test catches the wrong version of that, which is what
/// <see cref="ALiveSettingIsObeyedWithoutAnythingBeingRebuilt"/> is for.
/// </para>
/// </remarks>
public class LiveSettingConsumerTests
{
    public static TheoryData<string> ClassesWithASettingThatMustNotBeStale()
    {
        TheoryData<string> data = [];

        foreach (Type type in MustNotBeStale())
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Fact]
    public void ThereIsSomethingToCheck()
    {
        MustNotBeStale().Should().NotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ClassesWithASettingThatMustNotBeStale))]
    public void NoConsumerTakesAValueThatCanNeverChange(string optionsTypeName)
    {
        Type options = MustNotBeStale().Single(type => type.FullName == optionsTypeName);
        Type forbidden = typeof(IOptions<>).MakeGenericType(options);

        List<string> offenders = [];

        foreach (Assembly assembly in new[] { typeof(Program).Assembly, typeof(Data.StorageOptions).Assembly })
        {
            foreach (Type type in assembly.GetTypes())
            {
                foreach (ConstructorInfo constructor in type.GetConstructors(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Report(offenders, forbidden, type, constructor.Name, constructor.GetParameters());
                }

                foreach (MethodInfo method in type.GetMethods(
                             BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Report(offenders, forbidden, type, method.Name, method.GetParameters());
                }
            }
        }

        offenders.Should().BeEmpty(
            "{0} carries a setting graded live or deferred, so its consumers must take IOptionsMonitor or IOptionsSnapshot",
            options.Name);
    }

    /// <summary>
    /// The half reflection cannot see: that a live setting is actually re-read rather than captured
    /// once behind the right-looking type.
    /// </summary>
    [Fact]
    public async Task ALiveSettingIsObeyedWithoutAnythingBeingRebuilt()
    {
        IHostAddressResolver resolver = Substitute.For<IHostAddressResolver>();

        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]));

        ChangeableMonitor<CameraOptions> cameras =
            TestOptions.Monitor(new CameraOptions { RefuseLoopbackAndLinkLocal = true });

        CameraSourcePolicy policy = new(resolver,
                                        cameras,
                                        TestOptions.Monitor(new CertificateOptions()),
                                        TestOptions.Monitor(new PrusaConnect.PrusaConnectOptions()));

        CameraSourceCheck refused = await policy.CheckAsync("rtsp://camera.local/stream", CancellationToken.None);

        refused.IsAcceptable.Should().BeFalse("loopback is refused to begin with");

        cameras.Set(new CameraOptions { RefuseLoopbackAndLinkLocal = false });

        CameraSourceCheck allowed = await policy.CheckAsync("rtsp://camera.local/stream", CancellationToken.None);

        allowed.IsAcceptable
               .Should()
               .BeTrue("the same instance must obey the new value, with nothing rebuilt");
    }

    private static void Report(List<string> offenders,
                               Type forbidden,
                               Type owner,
                               string member,
                               ParameterInfo[] parameters)
    {
        foreach (ParameterInfo parameter in parameters)
        {
            if (parameter.ParameterType == forbidden)
            {
                offenders.Add($"{owner.FullName}.{member}({parameter.Name})");
            }
        }
    }

    private static IEnumerable<Type> MustNotBeStale()
    {
        return EditableSettings.All
                               .Where(setting => setting.Grade is SettingGrade.Live or SettingGrade.Deferred)
                               .Select(setting => setting.OptionsType)
                               .Distinct();
    }
}

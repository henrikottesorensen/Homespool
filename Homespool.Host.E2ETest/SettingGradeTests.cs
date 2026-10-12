using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Homespool.Host.Configuration;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Every editable setting against a real host: a save reaches what the setting's grade promises, and
/// nothing more.
/// </summary>
/// <remarks>
/// <para>
/// <b>One save of every setting, then one look at each.</b> A setting graded
/// <see cref="SettingGrade.Restart"/> must read as it did before the save through both the monitor
/// and a fresh snapshot - the configuration a consumer would bind from - while one that applies
/// without a restart must read as saved. Either way the page shows what was saved. A setting added to
/// <see cref="EditableSettings.All"/> is covered without a line here.
/// </para>
/// <para>
/// <b>What this cannot see is a consumer that copied a value when it was built</b>: a singleton
/// keeping <c>CurrentValue</c> in a field reads as live here and is not. The grade of a live setting
/// still rests on its reads having been checked, which is what the comment beside each entry
/// records.
/// </para>
/// </remarks>
public sealed class SettingGradeTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("setting-grades");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch);

        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public void ASaveReachesExactlyWhatEachSettingsGradePromises()
    {
        // Arrange - a different valid value for every setting
        Dictionary<string, string> before = InForce(fromSnapshot: false);
        Dictionary<string, string?> submitted = EditableSettings.All.ToDictionary(setting => setting.Path,
                                                                                  string? (setting) => Changed(setting, before[setting.Path]));

        // Act
        SettingsSaveResult result;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            result = scope.ServiceProvider.GetRequiredService<SettingsStore>()
                          .Save(submitted,
                                [.. EditableSettings.All.Where(setting => setting.NeedsConfirmingToEnable)
                                                        .Select(setting => setting.Path)]);
        }

        // Assert
        result.Errors.Should().BeEmpty("every changed value was chosen inside its range");
        result.Saved.Should().BeTrue();

        Dictionary<string, string> monitor = InForce(fromSnapshot: false);
        Dictionary<string, string> snapshot = InForce(fromSnapshot: true);
        IReadOnlyDictionary<string, string> shown;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            shown = scope.ServiceProvider.GetRequiredService<SettingsStore>().Current();
        }

        List<string> wrong = [];

        foreach (EditableSetting setting in EditableSettings.All)
        {
            string expected = setting.Grade == SettingGrade.Restart ? before[setting.Path] : submitted[setting.Path]!;
            string expectedShown = setting.IsSecret ? SettingsStore.SecretPlaceholder : submitted[setting.Path]!;

            if (monitor[setting.Path] != expected)
            {
                wrong.Add($"{setting.Path} ({setting.Grade}): the monitor reads '{monitor[setting.Path]}', expected '{expected}'");
            }

            if (snapshot[setting.Path] != expected)
            {
                wrong.Add($"{setting.Path} ({setting.Grade}): a snapshot reads '{snapshot[setting.Path]}', expected '{expected}'");
            }

            if (shown[setting.Path] != expectedShown)
            {
                wrong.Add($"{setting.Path} ({setting.Grade}): the page shows '{shown[setting.Path]}', expected '{expectedShown}'");
            }
        }

        wrong.Should().BeEmpty();
    }

    /// <summary>
    /// A value inside the property's range that is not <paramref name="current"/>.
    /// </summary>
    private static string Changed(EditableSetting setting, string current)
    {
        PropertyInfo property = setting.OptionsType.GetProperty(setting.Key)!;
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(bool))
        {
            return current == "true" ? "false" : "true";
        }

        if (type == typeof(string))
        {
            return "changed.example.test";
        }

        decimal value = decimal.Parse(current, CultureInfo.InvariantCulture);
        decimal maximum = property.GetCustomAttribute<RangeAttribute>() is RangeAttribute range ?
            Convert.ToDecimal(range.Maximum, CultureInfo.InvariantCulture) :
            decimal.MaxValue;

        decimal changed = value + 1 <= maximum ? value + 1 : value - 1;

        return changed.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What a consumer reads for every editable setting, formatted as the settings page formats it.
    /// </summary>
    private Dictionary<string, string> InForce(bool fromSnapshot)
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        Dictionary<string, string> values = [];

        foreach (EditableSetting setting in EditableSettings.All)
        {
            object options = fromSnapshot ?
                Value(scope.ServiceProvider, typeof(IOptionsSnapshot<>), setting.OptionsType, "Value") :
                Value(scope.ServiceProvider, typeof(IOptionsMonitor<>), setting.OptionsType, "CurrentValue");

            values[setting.Path] = setting.OptionsType.GetProperty(setting.Key)!.GetValue(options) switch
            {
                null => string.Empty,
                bool flag => flag ? "true" : "false",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                object value => value.ToString() ?? string.Empty,
            };
        }

        return values;
    }

    private static object Value(IServiceProvider services, Type accessor, Type optionsType, string property)
    {
        object options = services.GetRequiredService(accessor.MakeGenericType(optionsType));

        return options.GetType().GetProperty(property)!.GetValue(options)!;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Homespool.Host.Configuration;

/// <summary>
/// The settings file as a configuration layer, holding every <see cref="SettingGrade.Restart"/>
/// setting at the value it had when the process started.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes the restart grade true, rather than each consumer.</b> A save reloads the
/// whole configuration, and a restart-grade setting is one some part of the process fixed at startup -
/// the database telemetry is kept in, the passkey relying party, the mail sender. Anything reading
/// the same key through <c>IOptionsMonitor</c>, <c>IOptionsSnapshot</c>, an <c>IOptions</c>
/// resolved after the save or the configuration itself would see the next process's value beside
/// this one's state, and act on the mixture. Holding the key here means there is no such read to
/// get wrong: in this process the setting has one value, the one it started with.
/// </para>
/// <para>
/// <b>Live and deferred settings pass through untouched</b>, which is the reload's whole purpose.
/// </para>
/// <para>
/// <b>What was saved is still available, through <see cref="Saved"/></b> - for the settings page,
/// which shows what was typed, and for checks on what the next start will use. Nothing else should
/// read it.
/// </para>
/// <para>
/// <b>A reload swaps the data in one assignment, before announcing it.</b> A request binding
/// options on another thread sees either the previous contents or the new ones with the restart
/// keys already held, never the moment between.
/// </para>
/// </remarks>
public sealed class SettingsFileProvider : JsonConfigurationProvider
{
    private static readonly IChangeToken NeverChanges = new CancellationChangeToken(CancellationToken.None);

    /// <summary>
    /// Every key a restart-grade setting is read from. A secret has two: its encrypted form, which is
    /// what is stored, and its plain path, which an environment or a hand edit could supply.
    /// </summary>
    private static readonly string[] RestartKeys =
    [
        .. EditableSettings.All
                           .Where(setting => setting.Grade == SettingGrade.Restart)
                           .SelectMany(setting => setting.IsSecret ? [setting.Path, setting.StoredPath] : new[] { setting.Path })
                           .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    /// <summary>
    /// The restart keys this layer held at startup, and their values. A key absent here was absent
    /// from the file, and stays absent so that the layers beneath keep answering for it.
    /// </summary>
    private Dictionary<string, string?>? _atStartup;

    private Dictionary<string, string?> _saved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the layer.</summary>
    /// <param name="source">Where the file is, and how it is read.</param>
    public SettingsFileProvider(SettingsFileSource source)
        : base(source)
    {
    }

    /// <summary>
    /// Loads the file, or treats a missing one as empty.
    /// </summary>
    /// <remarks>
    /// A missing file is handled here rather than by the base, which would assign an empty
    /// dictionary and announce it before any restart key could be put back.
    /// </remarks>
    public override void Load()
    {
        IFileInfo? file = Source.FileProvider?.GetFileInfo(Source.Path ?? string.Empty);

        if (file is not null && file.Exists)
        {
            base.Load();

            return;
        }

        Apply(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        OnReload();
    }

    /// <inheritdoc />
    public override void Load(Stream stream)
    {
        Apply(JsonParser.Parse(stream));
    }

    /// <summary>
    /// A configuration over <paramref name="configuration"/> with this layer showing what is saved
    /// rather than what is in force.
    /// </summary>
    /// <param name="configuration">The application's configuration.</param>
    /// <returns>
    /// The saved view. Without a settings layer it answers exactly as
    /// <paramref name="configuration"/> does, nothing being held. Dispose it when done.
    /// </returns>
    /// <remarks>
    /// <b>Every layer beneath is read through, never loaded</b>: building a configuration from
    /// providers loads them, and loading the application's own would announce a reload to every
    /// options monitor in the process.
    /// </remarks>
    public static ConfigurationRoot Saved(IConfigurationRoot configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new ConfigurationRoot(
        [
            .. configuration.Providers.Select(layer => layer is SettingsFileProvider settings ?
                                                  new Fixed(settings._saved) :
                                                  (IConfigurationProvider)new ReadThrough(layer)),
        ]);
    }

    private void Apply(Dictionary<string, string?> saved)
    {
        Dictionary<string, string?> inForce = new(saved, StringComparer.OrdinalIgnoreCase);

        if (_atStartup is null)
        {
            _atStartup = RestartKeys.Where(saved.ContainsKey)
                                    .ToDictionary(key => key, key => saved[key], StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            foreach (string key in RestartKeys)
            {
                if (_atStartup.TryGetValue(key, out string? value))
                {
                    inForce[key] = value;
                }
                else
                {
                    inForce.Remove(key);
                }
            }
        }

        _saved = saved;
        Data = inForce;
    }

    /// <summary>
    /// Parses a JSON stream into the flat keys configuration uses, by the framework's own rules.
    /// </summary>
    private sealed class JsonParser : JsonStreamConfigurationProvider
    {
        private JsonParser()
            : base(new JsonStreamConfigurationSource())
        {
        }

        public static Dictionary<string, string?> Parse(Stream stream)
        {
            JsonParser parser = new();
            parser.Load(stream);

            return new Dictionary<string, string?>(parser.Data, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A layer answering from a fixed set of values, for the saved view.
    /// </summary>
    private sealed class Fixed : ConfigurationProvider
    {
        public Fixed(Dictionary<string, string?> values)
        {
            Data = values;
        }
    }

    /// <summary>
    /// Answers from another layer without loading it or listening to it.
    /// </summary>
    private sealed class ReadThrough(IConfigurationProvider inner) : IConfigurationProvider
    {
        public bool TryGet(string key, out string? value)
        {
            return inner.TryGet(key, out value);
        }

        public void Set(string key, string? value)
        {
            throw new NotSupportedException("The saved view is read-only.");
        }

        public IChangeToken GetReloadToken()
        {
            return NeverChanges;
        }

        public void Load()
        {
        }

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            return inner.GetChildKeys(earlierKeys, parentPath);
        }
    }
}

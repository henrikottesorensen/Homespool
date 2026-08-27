using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Homespool.Host.Configuration;

/// <summary>
/// The one file an administrator's settings are written to, and the only writable configuration
/// source. Read as an ordinary JSON configuration layer, written by the application.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is layered last, above the environment variables, and that is load-bearing.</b>
/// <c>compose.yaml</c> writes <c>Section__Property: "${VAR:-default}"</c>, and the shell substitutes
/// the default whether or not <c>.env</c> mentions the variable - so the environment always carries a
/// value for the keys compose names. Layered below, this file would be silently inert for exactly
/// those keys. The other half of that choice is that the migrated lines are removed from
/// <c>compose.yaml</c> outright, so a key has one home rather than two with a winner nobody can see.
/// </para>
/// <para>
/// <b>The layer is registered without <c>reloadOnChange</c>, and picking the file up again is an
/// explicit <c>Reload</c> after a write.</b> Watching costs an inotify instance per configuration
/// provider, and the default ceiling is 128 per user - low enough that the end-to-end suite, which
/// starts a host per test class against one shared content root, exhausted it and left 167 tests
/// failing with nothing but "the entry point exited without ever building an IHost". Reloading on
/// demand also makes the refresh happen exactly once per save, where a watcher fires twice, and
/// removes the need for the file's directory to exist before the application starts. What it costs is
/// that editing this file by hand takes effect at the next restart - acceptable for a file the
/// application owns and the page maintains.
/// </para>
/// <para>
/// <b>Written by rename, never in place.</b> A reload reads whatever is on disk at that moment, and
/// a half-written file binds as readily as a whole one. Writing a temporary file in the same
/// directory and moving it over the target makes the swap atomic, because a rename within one
/// filesystem is.
/// </para>
/// <para>
/// <b>Mode 0600, and the file holds a credential.</b> The SMTP password lives here as ciphertext
/// rather than in the clear, but the permission bits are what keep it from being read by anything
/// else sharing the volume in the first place. Windows has no equivalent and needs none: the
/// deployment is a Linux container, and the development case is one person's own machine.
/// </para>
/// </remarks>
public sealed class SettingsFile
{
    /// <summary>
    /// The configuration key naming where this file lives. It cannot itself be set in the file.
    /// </summary>
    public const string PathConfigurationKey = "Settings:File";

    /// <summary>
    /// Where the file lives when nothing says otherwise, relative to the content root - which is
    /// <c>/app</c> in the container, so this is the mounted <c>homespool-data</c> volume.
    /// </summary>
    public const string DefaultRelativePath = "data/settings.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>
    /// Creates an accessor for the settings file.
    /// </summary>
    /// <param name="path">The resolved absolute path to the file.</param>
    public SettingsFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
    }

    /// <summary>The absolute path of the settings file, whether or not it exists yet.</summary>
    public string Path => _path;

    /// <summary>Whether the file exists.</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>
    /// Resolves where the settings file belongs, following the same rule as every other configurable
    /// path here: absolute is taken as given, relative is taken from the content root.
    /// </summary>
    /// <param name="configuredPath">The configured path, or null or empty to use the default.</param>
    /// <param name="contentRootPath">The content root a relative path is resolved against.</param>
    /// <returns>An absolute path.</returns>
    public static string Resolve(string? configuredPath, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        string relativeOrAbsolute = string.IsNullOrWhiteSpace(configuredPath) ?
            DefaultRelativePath :
            configuredPath;

        return System.IO.Path.IsPathRooted(relativeOrAbsolute) ?
            relativeOrAbsolute :
            System.IO.Path.Combine(contentRootPath, relativeOrAbsolute);
    }

    /// <summary>
    /// Reads the file's current contents.
    /// </summary>
    /// <returns>
    /// The stored settings, or an empty object when the file is absent, empty, or holds anything
    /// other than a JSON object.
    /// </returns>
    /// <remarks>
    /// <b>Unreadable is treated as absent rather than fatal.</b> The alternative is a deployment that
    /// will not start because one hand-edited brace is wrong, with no way in to fix it - and the
    /// startup path here has no interface to explain itself. What a bad file costs instead is the
    /// settings reverting to their configured defaults, which is visible on the page and correctable
    /// there.
    /// </remarks>
    public JsonObject Read()
    {
        if (!File.Exists(_path))
        {
            return new JsonObject();
        }

        try
        {
            string json = File.ReadAllText(_path);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new JsonObject();
            }

            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
        catch (IOException)
        {
            return new JsonObject();
        }
    }

    /// <summary>
    /// Writes the file, replacing whatever was there.
    /// </summary>
    /// <param name="contents">The settings to store, as a nested object matching the configuration shape.</param>
    public void Write(JsonObject contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        EnsureDirectory();

        // Same directory as the target, so the move below is a rename within one filesystem and
        // therefore atomic. A temporary directory would make it a copy, which is what this avoids.
        string temporaryPath = _path + ".tmp";

        File.WriteAllText(temporaryPath, contents.ToJsonString(WriteOptions));
        Restrict(temporaryPath);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private void EnsureDirectory()
    {
        string? directory = System.IO.Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

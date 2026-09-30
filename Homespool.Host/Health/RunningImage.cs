namespace Homespool.Host.Health;

/// <summary>
/// Which image this process is running from, as far as it can tell from inside: the commit it was
/// built from and the base image digest it was built on.
/// </summary>
/// <remarks>
/// <para>
/// A process cannot read its own image's labels or digest, so these are the two facts the build
/// stamps into it by other routes: the commit into the assembly, and the base into the
/// <c>HOMESPOOL_IMAGE_BASE</c> environment variable. They are the same values the image's
/// <c>org.opencontainers.image.revision</c> and <c>…base.digest</c> labels carry, which is what makes
/// them comparable with what the host's update check read from the running container.
/// </para>
/// <para>
/// Either may be unknown - a build given no git ref, or one given no base digest - and an unknown
/// value compares with nothing.
/// </para>
/// </remarks>
/// <param name="Revision">The commit, as the revision label spells it.</param>
/// <param name="BaseDigest">The base image's digest, <c>sha256:…</c>.</param>
public sealed record RunningImage(string? Revision, string? BaseDigest)
{
    /// <summary>The environment variable the application image sets to its base, <c>tag@digest</c>.</summary>
    public const string ImageBaseVariable = "HOMESPOOL_IMAGE_BASE";

    /// <summary>This image, from a commit and the value of <see cref="ImageBaseVariable"/>.</summary>
    /// <param name="revision">The commit this build was made from, if known.</param>
    /// <param name="imageBase">The base as the image records it: <c>tag@digest</c>, or the tag alone.</param>
    /// <returns>The image, with the digest taken from after the <c>@</c>.</returns>
    public static RunningImage From(string? revision, string? imageBase)
    {
        int at = imageBase?.LastIndexOf('@') ?? -1;

        return new(revision, at < 0 ? null : imageBase![(at + 1)..]);
    }
}

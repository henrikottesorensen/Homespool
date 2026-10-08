using System;
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.PrintFiles;

/// <summary>
/// The content digest a print file's row carries: base64url SHA-384 of its bytes.
/// </summary>
/// <remarks>
/// <b>One implementation for both writers.</b> An upload hashes the bytes as they stream past on their
/// way to disk; <see cref="PrintFileReconciler"/> hashes files already there. The reprint check compares
/// a digest recorded by one with a digest recorded by the other, so they have to be the same string for
/// the same bytes - by construction, not by two loops that happen to agree. SHA-384 rather than
/// SHA-256 - see <see cref="Model.Entities.HSFile.Digest"/>.
/// </remarks>
public static class PrintFileDigest
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Reads <paramref name="content"/> to its end and returns its digest, writing every byte to
    /// <paramref name="copyTo"/> on the same pass when there is one.
    /// </summary>
    public static async Task<string> ComputeAsync(Stream content, Stream? copyTo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA384);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            int read;

            while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                if (copyTo is not null)
                {
                    await copyTo.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                hash.AppendData(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Base64Url.EncodeToString(hash.GetHashAndReset());
    }
}

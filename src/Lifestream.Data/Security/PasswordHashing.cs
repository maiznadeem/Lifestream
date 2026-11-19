using System.Buffers.Binary;
using Lifestream.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Lifestream.Data.Security;

/// <summary>
/// Staff passwords: ASP.NET Core Identity v3 format (PBKDF2-HMAC-SHA512, random 128-bit salt, 256-bit subkey),
/// at least <see cref="MinIterations"/> iterations. The database column holds nothing else.
/// </summary>
public static class PasswordHashing
{
    public const int MinIterations = 600_000;

    private static readonly PasswordHasher<Staff> Hasher = new(Options.Create(new PasswordHasherOptions
    {
        CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
        IterationCount = MinIterations,
    }));

    public static string Hash(string password) => Hasher.HashPassword(new Staff(), password);

    public static bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(new Staff(), hash, password) is not PasswordVerificationResult.Failed;

    /// <summary>
    /// True when the text is a well-formed salted hash: Identity v3 marker, PRF HMAC-SHA512, at least
    /// <see cref="MinIterations"/> iterations, salt of at least 16 bytes, subkey of at least 32 bytes, nothing left over.
    /// A plain-text password, an empty string or a truncated value is not valid.
    /// </summary>
    public static bool IsValidHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length > 256)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[192];
        if (!Convert.TryFromBase64String(hash, bytes, out var written) || written < 13)
        {
            return false;
        }

        bytes = bytes[..written];
        if (bytes[0] != 0x01)
        {
            return false;
        }

        var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes[1..]);
        var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes[5..]);
        var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[9..]);
        if (prf != 2 || iterations < MinIterations || saltLength < 16 || saltLength > 64)
        {
            return false;
        }

        var subkeyLength = written - 13 - (int)saltLength;
        return subkeyLength is >= 32 and <= 128;
    }
}

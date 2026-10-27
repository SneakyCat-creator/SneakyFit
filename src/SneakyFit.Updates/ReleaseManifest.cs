using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SneakyFit.Updates;

public sealed record ExecutableRelease(string Product, string Version, string Architecture, long Length, string Sha256);
public sealed record SignedRelease(string Payload, string Signature);

public static class ReleaseTrust
{
    public const string Product = "SneakyFit";
    public const long MaxPackageSize = 256L * 1024 * 1024;
    public const int MaxEnvelopeSize = 32 * 1024;
    public static ReadOnlySpan<byte> TrailerMagic => "SFTSIG01"u8;
    public const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAzXYMaNTev1wWCAF7fcFA
        D31TL9HzbAa6Ta/tF/y8wEdnNpSPCc83e7Xsdv2fvCrKascYt+KCla/+5JBxMVL8
        AF6CQLJ15dyTBpdutMHQiok0gI26iGhpH7up2zdk9Ru/puS0jQjN4fUigEXzf3sb
        NE+mEdJ8AKEKG17YzxTa25WNLGf99EG4tng/AH6yedhNTraEHvSXpNhPKO2uy79F
        ENMTzX/X6FJwIVmlqd4hr6fMrbh9ckdNjE3YRZBdjqpcz/zLcbdQ3LpA3N7tvLZN
        cCH6WhZHvlJvGTgka1tridTRSecX+3pUVd+xK2ecELlV+iGcFWTfwstNxC4AqBLW
        MonFM2FJqv3UNqJvMGq4UKAuwpWH5spnipFdo5Dz9iMZgGz2lyWtiTq5lasIMjMT
        g0g0P1Krzcn1T8OrD4Az5OgCB0GEZDyxrIK2Yw+XaBxad0v/aFxPVBJfHggG1bQY
        m2k73Q1/O5t6am8ZNzudB6YXsSDCj8phBQYP2ZBT/DDJAgMBAAE=
        -----END PUBLIC KEY-----
        """;
    public static ExecutableRelease VerifyExecutable(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return VerifyExecutable(input);
    }

    public static ExecutableRelease VerifyExecutable(Stream input)
    {
        if (!input.CanSeek || input.Length is < 1024 or > MaxPackageSize)
            throw new InvalidDataException("Неподдерживаемый файл обновления.");
        Span<byte> footer = stackalloc byte[12];
        input.Position = input.Length - footer.Length; input.ReadExactly(footer);
        if (!footer[4..].SequenceEqual(TrailerMagic)) throw new InvalidDataException("Нет подписи SneakyFit.");
        var envelopeLength = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        if (envelopeLength is < 1 or > MaxEnvelopeSize || envelopeLength + 12 >= input.Length)
            throw new InvalidDataException("Подпись обновления повреждена.");
        var payloadLength = input.Length - envelopeLength - 12;
        input.Position = payloadLength;
        var envelope = new byte[(int)envelopeLength]; input.ReadExactly(envelope);
        var signed = JsonSerializer.Deserialize<SignedRelease>(envelope) ?? throw new InvalidDataException("Подпись обновления повреждена.");
        var payload = Convert.FromBase64String(signed.Payload);
        using var rsa = RSA.Create(); rsa.ImportFromPem(PublicKey);
        if (!rsa.VerifyData(payload, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("Подпись обновления не совпадает.");
        var release = JsonSerializer.Deserialize<ExecutableRelease>(payload) ?? throw new InvalidDataException("Пустая подпись обновления.");
        if (release.Product != Product || release.Architecture != "win-x64" ||
            !System.Version.TryParse(release.Version, out var version) || version.Major < 1 ||
            release.Length != payloadLength || !IsHash(release.Sha256))
            throw new InvalidDataException("Неподдерживаемое обновление.");
        input.Position = 0;
        if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new InvalidDataException("Некорректный файл приложения.");
        input.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; long remaining = payloadLength;
        while (remaining > 0)
        {
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (count == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, count); remaining -= count;
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл обновления повреждён.");
        return release;
    }

    public static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    public static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    public static bool Matches(string path, long size, string hash) => File.Exists(path)
        && new FileInfo(path).Length == size && HashFile(path).Equals(hash, StringComparison.OrdinalIgnoreCase);
}
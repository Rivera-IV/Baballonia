using System;
using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services;

public sealed record EncryptedDiagnosticPackage(
    string FilePath,
    string Sha256,
    long Size);

/// <summary>
/// Encrypts a local diagnostic ZIP before it can leave the computer. Only the public key is
/// shipped with Baballonia; the private key is kept by the maintainer outside the repository.
/// </summary>
public sealed class DiagnosticPackageEncryptor
{
    public const string KeyId = "baballonia-diagnostics-2026-08-v1";
    public const long MaximumPlaintextBytes = 74L * 1024 * 1024;

    private static readonly byte[] Magic = "BABDIAG1"u8.ToArray();
    private const string PublicKeyResource =
        "Baballonia.Assets.DiagnosticUploadPublicKey.pem";
    private readonly string _publicKey;

    public DiagnosticPackageEncryptor()
        : this(ReadEmbeddedPublicKey())
    {
    }

    public DiagnosticPackageEncryptor(string publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        _publicKey = publicKey;
    }

    public async Task<EncryptedDiagnosticPackage> EncryptAsync(
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        var zip = new FileInfo(zipPath);
        if (!zip.Exists)
            throw new FileNotFoundException("The diagnostic ZIP no longer exists.", zipPath);
        if (zip.Length <= 0 || zip.Length > MaximumPlaintextBytes)
            throw new InvalidDataException(
                $"The diagnostic ZIP must be between 1 byte and {MaximumPlaintextBytes} bytes.");

        var plaintext = await File.ReadAllBytesAsync(zip.FullName, cancellationToken);
        var contentKey = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        byte[]? ciphertext = null;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publicKey);
            var wrappedKey = rsa.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA256);
            var header = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                keyId = KeyId,
                keyWrap = "RSA-OAEP-SHA256",
                contentCipher = "AES-256-GCM",
                wrappedKey = Convert.ToBase64String(wrappedKey),
                nonce = Convert.ToBase64String(nonce),
                payload = "application/zip",
            });
            if (header.Length > 64 * 1024)
                throw new InvalidDataException("The encrypted diagnostic header is too large.");

            var headerLength = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(headerLength, (uint)header.Length);
            var associatedData = new byte[Magic.Length + headerLength.Length + header.Length];
            Magic.CopyTo(associatedData, 0);
            headerLength.CopyTo(associatedData, Magic.Length);
            header.CopyTo(associatedData, Magic.Length + headerLength.Length);

            ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(contentKey, tag.Length))
                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            var outputPath = UniqueEncryptedPath(zip);
            var temporaryPath = outputPath + ".partial";
            try
            {
                await using (var output = new FileStream(
                                 temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 bufferSize: 81920, useAsync: true))
                {
                    await output.WriteAsync(Magic, cancellationToken);
                    await output.WriteAsync(headerLength, cancellationToken);
                    await output.WriteAsync(header, cancellationToken);
                    await output.WriteAsync(ciphertext, cancellationToken);
                    await output.WriteAsync(tag, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                File.Move(temporaryPath, outputPath);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }

            await using var encryptedStream = new FileStream(
                outputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(encryptedStream, cancellationToken));
            return new EncryptedDiagnosticPackage(outputPath, sha256, encryptedStream.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
            CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext != null)
                CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static string ReadEmbeddedPublicKey()
    {
        using var stream = typeof(DiagnosticPackageEncryptor).Assembly
            .GetManifestResourceStream(PublicKeyResource)
            ?? throw new InvalidOperationException(
                "The diagnostic upload public key is missing from this build.");
        using var reader = new StreamReader(stream, Encoding.ASCII, false);
        return reader.ReadToEnd();
    }

    private static string UniqueEncryptedPath(FileInfo zip)
    {
        var directory = zip.DirectoryName ?? throw new InvalidDataException("Invalid ZIP path.");
        var stem = Path.GetFileNameWithoutExtension(zip.Name);
        var path = Path.Combine(directory, stem + ".babdiag");
        for (var suffix = 2; File.Exists(path) || File.Exists(path + ".partial"); suffix++)
            path = Path.Combine(directory, $"{stem}-{suffix}.babdiag");
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

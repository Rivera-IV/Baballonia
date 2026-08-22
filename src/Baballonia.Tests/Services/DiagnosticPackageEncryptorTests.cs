using Baballonia.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Baballonia.Tests.Services;

[TestClass]
public class DiagnosticPackageEncryptorTests
{
    [TestMethod]
    public async Task EncryptAsync_CreatesAuthenticatedContainerDecryptableOnlyWithPrivateKey()
    {
        var directory = Directory.CreateTempSubdirectory("baballonia-encryption-test-");
        try
        {
            var zipPath = Path.Combine(directory.FullName, "report.zip");
            var expected = RandomNumberGenerator.GetBytes(4096);
            await File.WriteAllBytesAsync(zipPath, expected);
            using var rsa = RSA.Create(3072);
            var encryptor = new DiagnosticPackageEncryptor(rsa.ExportSubjectPublicKeyInfoPem());

            var result = await encryptor.EncryptAsync(zipPath);

            var container = await File.ReadAllBytesAsync(result.FilePath);
            CollectionAssert.AreEqual("BABDIAG1"u8.ToArray(), container[..8]);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(container)), result.Sha256);
            var headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8, 4)));
            var header = JsonSerializer.Deserialize<Header>(
                container.AsSpan(12, headerLength),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.IsNotNull(header);
            Assert.AreEqual(DiagnosticPackageEncryptor.KeyId, header.KeyId);

            var contentKey = rsa.Decrypt(
                Convert.FromBase64String(header.WrappedKey), RSAEncryptionPadding.OaepSHA256);
            try
            {
                var offset = 12 + headerLength;
                var ciphertextLength = container.Length - offset - 16;
                var actual = new byte[ciphertextLength];
                using var aes = new AesGcm(contentKey, 16);
                aes.Decrypt(
                    Convert.FromBase64String(header.Nonce),
                    container.AsSpan(offset, ciphertextLength),
                    container.AsSpan(offset + ciphertextLength, 16),
                    actual,
                    container.AsSpan(0, offset));
                CollectionAssert.AreEqual(expected, actual);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contentKey);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed record Header(string KeyId, string WrappedKey, string Nonce);
}

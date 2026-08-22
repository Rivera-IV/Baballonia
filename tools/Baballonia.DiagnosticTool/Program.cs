using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const string KeyId = "baballonia-diagnostics-2026-08-v1";
var defaultPrivatePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Baballonia Diagnostics", "Keys", KeyId + ".pkcs8.dpapi");

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: generate <public.pem> [private.dpapi] | verify-key <public.pem> [private.dpapi] | export-recovery <private.pem> [private.dpapi] | decrypt <report.babdiag> <output.zip> [private.dpapi]");
    return 2;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "generate" when args.Length is 2 or 3:
            Generate(Path.GetFullPath(args[1]), args.Length == 3 ? Path.GetFullPath(args[2]) : defaultPrivatePath);
            break;
        case "decrypt" when args.Length is 3 or 4:
            Decrypt(
                Path.GetFullPath(args[1]),
                Path.GetFullPath(args[2]),
                args.Length == 4 ? Path.GetFullPath(args[3]) : defaultPrivatePath);
            break;
        case "verify-key" when args.Length is 2 or 3:
            VerifyKeyPair(
                Path.GetFullPath(args[1]),
                args.Length == 3 ? Path.GetFullPath(args[2]) : defaultPrivatePath);
            break;
        case "export-recovery" when args.Length is 2 or 3:
            ExportRecoveryKey(
                Path.GetFullPath(args[1]),
                args.Length == 3 ? Path.GetFullPath(args[2]) : defaultPrivatePath);
            break;
        default:
            throw new ArgumentException("Invalid command or argument count.");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void ExportRecoveryKey(string outputPath, string privatePath)
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("DPAPI key protection currently requires Windows.");
    if (File.Exists(outputPath))
        throw new IOException("Refusing to replace an existing recovery key.");

    var firstPassword = ReadPassword("Recovery-key password: ");
    var secondPassword = ReadPassword("Repeat password: ");
    byte[]? privateBytes = null;
    try
    {
        if (firstPassword.Length < 14)
            throw new ArgumentException("Use a recovery password with at least 14 characters.");
        if (!firstPassword.AsSpan().SequenceEqual(secondPassword))
            throw new ArgumentException("The recovery passwords do not match.");

        var protectedPrivate = File.ReadAllBytes(privatePath);
        try
        {
            privateBytes = ProtectedData.Unprotect(
                protectedPrivate, Encoding.UTF8.GetBytes(KeyId), DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPrivate);
        }

        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(privateBytes, out _);
        var encryptedPem = rsa.ExportEncryptedPkcs8PrivateKeyPem(
            firstPassword,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, encryptedPem + Environment.NewLine, new UTF8Encoding(false));
        Console.WriteLine($"Password-protected recovery key: {outputPath}");
        Console.WriteLine("Store this file and its password separately in secure locations.");
    }
    finally
    {
        Array.Clear(firstPassword);
        Array.Clear(secondPassword);
        if (privateBytes != null) CryptographicOperations.ZeroMemory(privateBytes);
    }
}

static char[] ReadPassword(string prompt)
{
    Console.Write(prompt);
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return characters.ToArray();
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
    }
}

static void VerifyKeyPair(string publicPath, string privatePath)
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("DPAPI key protection currently requires Windows.");
    var protectedPrivate = File.ReadAllBytes(privatePath);
    byte[]? privateBytes = null;
    try
    {
        privateBytes = ProtectedData.Unprotect(
            protectedPrivate, Encoding.UTF8.GetBytes(KeyId), DataProtectionScope.CurrentUser);
        using var privateKey = RSA.Create();
        privateKey.ImportPkcs8PrivateKey(privateBytes, out _);
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(File.ReadAllText(publicPath));
        if (!CryptographicOperations.FixedTimeEquals(
                privateKey.ExportSubjectPublicKeyInfo(),
                publicKey.ExportSubjectPublicKeyInfo()))
            throw new CryptographicException("The public and protected private keys do not match.");
        Console.WriteLine("The diagnostic public and private keys match.");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(protectedPrivate);
        if (privateBytes != null) CryptographicOperations.ZeroMemory(privateBytes);
    }
}

static void Generate(string publicPath, string privatePath)
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("DPAPI key protection currently requires Windows.");
    if (File.Exists(publicPath) || File.Exists(privatePath))
        throw new IOException("Refusing to replace an existing diagnostic key.");

    Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
    using var rsa = RSA.Create(3072);
    var privateBytes = rsa.ExportPkcs8PrivateKey();
    try
    {
        var protectedPrivate = ProtectedData.Protect(
            privateBytes, Encoding.UTF8.GetBytes(KeyId), DataProtectionScope.CurrentUser);
        try
        {
            File.WriteAllBytes(privatePath, protectedPrivate);
            File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem() + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            if (File.Exists(privatePath)) File.Delete(privatePath);
            if (File.Exists(publicPath)) File.Delete(publicPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPrivate);
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(privateBytes);
    }

    Console.WriteLine($"Public key: {publicPath}");
    Console.WriteLine($"DPAPI-protected private key: {privatePath}");
    Console.WriteLine("Back up the protected key together with the Windows account/profile recovery material.");
}

static void Decrypt(string reportPath, string outputPath, string privatePath)
{
    if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("DPAPI key protection currently requires Windows.");
    if (!File.Exists(reportPath)) throw new FileNotFoundException("Encrypted report not found.", reportPath);
    if (!File.Exists(privatePath)) throw new FileNotFoundException("Protected private key not found.", privatePath);
    if (File.Exists(outputPath)) throw new IOException("Refusing to replace an existing output ZIP.");

    var container = File.ReadAllBytes(reportPath);
    var magic = "BABDIAG1"u8;
    byte[]? privateBytes = null;
    byte[]? contentKey = null;
    byte[]? plaintext = null;
    try
    {
        if (container.Length < 29 || !container.AsSpan(0, magic.Length).SequenceEqual(magic))
            throw new InvalidDataException("Invalid or incomplete diagnostic container.");
        var headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(8, 4)));
        if (headerLength is <= 0 or > 65536 || 12 + headerLength + 16 > container.Length)
            throw new InvalidDataException("Invalid diagnostic header length.");

        var payloadOffset = 12 + headerLength;
        var header = JsonSerializer.Deserialize<Header>(container.AsSpan(12, headerLength), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidDataException("Invalid diagnostic header.");
        if (header.Version != 1 || header.KeyId != KeyId || header.KeyWrap != "RSA-OAEP-SHA256" ||
            header.ContentCipher != "AES-256-GCM" || header.Payload != "application/zip")
            throw new InvalidDataException("Unsupported diagnostic cryptographic format.");

        var protectedPrivate = File.ReadAllBytes(privatePath);
        try
        {
            privateBytes = ProtectedData.Unprotect(
                protectedPrivate, Encoding.UTF8.GetBytes(KeyId), DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPrivate);
        }

        using (var rsa = RSA.Create())
        {
            rsa.ImportPkcs8PrivateKey(privateBytes, out _);
            contentKey = rsa.Decrypt(Convert.FromBase64String(header.WrappedKey), RSAEncryptionPadding.OaepSHA256);
        }

        var nonce = Convert.FromBase64String(header.Nonce);
        var cipherLength = container.Length - payloadOffset - 16;
        plaintext = new byte[cipherLength];
        using (var aes = new AesGcm(contentKey, 16))
        {
            aes.Decrypt(
                nonce,
                container.AsSpan(payloadOffset, cipherLength),
                container.AsSpan(payloadOffset + cipherLength, 16),
                plaintext,
                container.AsSpan(0, payloadOffset));
        }
        File.WriteAllBytes(outputPath, plaintext);
        Console.WriteLine($"Decrypted ZIP: {outputPath}");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(container);
        if (privateBytes != null) CryptographicOperations.ZeroMemory(privateBytes);
        if (contentKey != null) CryptographicOperations.ZeroMemory(contentKey);
        if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext);
    }
}

internal sealed record Header(
    int Version,
    string KeyId,
    string KeyWrap,
    string ContentCipher,
    string WrappedKey,
    string Nonce,
    string Payload);

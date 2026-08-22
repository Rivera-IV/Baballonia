using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services;

public sealed record DiagnosticUploadReceipt(
    string ReportId,
    string DeleteToken,
    DateTimeOffset ExpiresAt,
    string LocalReceiptPath);

public sealed class DiagnosticReportUploadService(HttpClient client)
{
    public const string MediaType = "application/vnd.baballonia.diagnostic+encrypted";

    public async Task<DiagnosticUploadReceipt> UploadAsync(
        EncryptedDiagnosticPackage package,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(package.FilePath);
        if (!file.Exists || file.Length != package.Size)
            throw new FileNotFoundException("The encrypted diagnostic package is missing or changed.", package.FilePath);

        await using var stream = new FileStream(
            file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(MediaType);
        content.Headers.ContentLength = file.Length;
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/reports") { Content = content };
        request.Headers.TryAddWithoutValidation("X-Baballonia-Format", "1");
        request.Headers.TryAddWithoutValidation("X-Baballonia-Consent", "eye-images-v1");
        request.Headers.TryAddWithoutValidation("X-Content-SHA256", package.Sha256);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new InvalidDataException("The encrypted report exceeds the server upload limit.");
        if (!response.IsSuccessStatusCode)
        {
            var apiError = TryDeserialize<ApiError>(responseBody);
            throw new HttpRequestException(
                apiError?.Message ?? $"Upload failed with HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);
        }

        var result = TryDeserialize<UploadResponse>(responseBody);
        if (result is not { Ok: true } || string.IsNullOrWhiteSpace(result.ReportId) ||
            !Guid.TryParseExact(result.ReportId, "D", out _) ||
            result.DeleteToken.Length != 64 || !IsHex(result.DeleteToken) ||
            result.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The report service returned an invalid receipt.");

        var receiptPath = await SaveReceiptAsync(file.DirectoryName, result, cancellationToken);
        return new DiagnosticUploadReceipt(
            result.ReportId, result.DeleteToken, result.ExpiresAt, receiptPath);
    }

    public async Task DeleteAsync(
        string reportId,
        string deleteToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"v1/reports/{reportId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deleteToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new HttpRequestException(
                $"Report deletion failed with HTTP {(int)response.StatusCode}.",
                null, response.StatusCode);
    }

    private static async Task<string> SaveReceiptAsync(
        string? directory,
        UploadResponse result,
        CancellationToken cancellationToken)
    {
        directory ??= Path.GetTempPath();
        var path = Path.Combine(directory, $"Baballonia-Diagnostic-Receipt-{result.ReportId}.json");
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            result.ReportId,
            result.ExpiresAt,
            result.DeleteToken,
            note = "Keep this receipt if you want to delete the uploaded encrypted report early.",
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken);
        return path;
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    private sealed record UploadResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("reportId")] string ReportId,
        [property: JsonPropertyName("deleteToken")] string DeleteToken,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

    private sealed record ApiError(
        [property: JsonPropertyName("message")] string Message);
}

using Baballonia.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Tests.Services;

[TestClass]
public class DiagnosticReportUploadServiceTests
{
    [TestMethod]
    public async Task UploadAsync_SendsRequiredConsentAndSavesDeletionReceipt()
    {
        var directory = Directory.CreateTempSubdirectory("baballonia-upload-test-");
        try
        {
            var packagePath = Path.Combine(directory.FullName, "report.babdiag");
            await File.WriteAllBytesAsync(packagePath, RandomNumberGenerator.GetBytes(768));
            var handler = new CapturingHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://diagnostics.example/") };
            var service = new DiagnosticReportUploadService(client);
            var package = new EncryptedDiagnosticPackage(packagePath, "A".PadLeft(64, 'A'), 768);

            var receipt = await service.UploadAsync(package);

            Assert.AreEqual("eye-images-v1", handler.Consent);
            Assert.AreEqual("1", handler.Format);
            Assert.AreEqual(768L, handler.ContentLength);
            Assert.AreEqual("f47ac10b-58cc-4372-a567-0e02b2c3d479", receipt.ReportId);
            Assert.IsTrue(File.Exists(receipt.LocalReceiptPath));
            StringAssert.Contains(
                await File.ReadAllTextAsync(receipt.LocalReceiptPath),
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Consent { get; private set; }
        public string? Format { get; private set; }
        public long? ContentLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Consent = request.Headers.GetValues("X-Baballonia-Consent").Single();
            Format = request.Headers.GetValues("X-Baballonia-Format").Single();
            ContentLength = request.Content?.Headers.ContentLength;
            Assert.AreEqual(768, (await request.Content!.ReadAsByteArrayAsync(cancellationToken)).Length);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"ok\":true,\"reportId\":\"f47ac10b-58cc-4372-a567-0e02b2c3d479\",\"deleteToken\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"expiresAt\":\"2030-09-05T10:00:00Z\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}

using System;
using System.IO;
using Baballonia.Desktop.Calibration;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Baballonia.Tests.Trainer;

[TestClass]
[TestSubject(typeof(TrainerService))]
public class TrainerServiceTest
{
    [TestMethod]
    public void RunTraining_WithMissingCapture_ThrowsBeforeStartingProcess()
    {
        using var factory = LoggerFactory.Create(builder => builder.AddConsole().AddDebug());
        using var service = new TrainerService(factory.CreateLogger<TrainerService>());
        var missingCapture = Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.bin");

        Assert.ThrowsExactly<FileNotFoundException>(() =>
            service.RunTraining(missingCapture, Path.Combine(Path.GetTempPath(), "model.onnx")));
    }
}

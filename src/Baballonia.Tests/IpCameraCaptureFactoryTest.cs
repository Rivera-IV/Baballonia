using Baballonia.IPCameraCapture;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Baballonia.Tests;

[TestClass]
[TestSubject(typeof(IpCameraCaptureFactory))]
public class IpCameraCaptureFactoryTest
{
    [TestMethod]
    public void CanConnectShouldFail()
    {
        using LoggerFactory loggerFactory = new();
        IpCameraCaptureFactory factory = new(loggerFactory);

        Assert.IsFalse(factory.CanConnect("OBS Virtual Camera"));
    }
}

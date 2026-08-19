using Baballonia.Services.Inference;
using Baballonia.Services.Inference.Enums;
using OpenCvSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Threading;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class DualCameraSourceTest
{
    [TestMethod]
    public void StartAndStop_ForwardToMatchingCameraOperation()
    {
        var left = new StubVideoSource();
        var right = new StubVideoSource();
        using var source = new DualCameraSource { LeftCam = left, RightCam = right };

        Assert.IsTrue(source.Start());
        Assert.AreEqual(1, left.StartCalls);
        Assert.AreEqual(1, right.StartCalls);
        Assert.AreEqual(0, left.StopCalls);
        Assert.AreEqual(0, right.StopCalls);

        Assert.IsTrue(source.Stop());
        Assert.AreEqual(1, left.StopCalls);
        Assert.AreEqual(1, right.StopCalls);
    }

    private sealed class StubVideoSource : IVideoSource
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool Start() { StartCalls++; return true; }
        public bool Stop() { StopCalls++; return true; }
        public Mat? GetFrame(ColorType? color = null) => null;
        public WaitHandle[] GetFrameWaitHandles() => [];
        public void Dispose() { }
    }
}

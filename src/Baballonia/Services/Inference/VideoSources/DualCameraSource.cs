using Baballonia.Services.Inference.Enums;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Baballonia.Services.Inference;

public class DualCameraSource : IVideoSource
{
    public IVideoSource? LeftCam;
    public IVideoSource? RightCam;

    private Mat? LastLeftImage;
    private Mat? LastRightImage;

    public bool Start()
    {
        if (ReferenceEquals(LeftCam, RightCam))
            return LeftCam?.Start() ?? true;
        return (LeftCam?.Start() ?? true) && (RightCam?.Start() ?? true);
    }

    public bool Stop()
    {
        if (ReferenceEquals(LeftCam, RightCam))
            return LeftCam?.Stop() ?? true;
        return (LeftCam?.Stop() ?? true) && (RightCam?.Stop() ?? true);
    }

    public WaitHandle[] GetFrameWaitHandles()
    {
        var handles = new List<WaitHandle>(2);
        if (LeftCam != null) handles.AddRange(LeftCam.GetFrameWaitHandles());
        if (RightCam != null) handles.AddRange(RightCam.GetFrameWaitHandles());
        return handles.ToArray();
    }

    // Here we try to acquire 2 images from both cameras and stitch them into a single image
    // if at least one image can be acquired, try to stitch it with last second image
    public Mat? GetFrame(ColorType? color = null)
    {
        var leftImage = LeftCam?.GetFrame(color);
        var rightImage = RightCam?.GetFrame(color);
        var ownsLeftImage = leftImage != null;
        var ownsRightImage = rightImage != null;

        if (leftImage == null && rightImage == null)
            return null;

        // Track which images are new vs fallback
        var leftIsNew = leftImage != null;
        var rightIsNew = rightImage != null;

        leftImage ??= LastLeftImage;
        rightImage ??= LastRightImage;


        try
        {
            switch (leftImage)
            {
                case null when rightImage == null:
                    return null;
                case null:
                    leftImage = rightImage.Clone();
                    ownsLeftImage = true;
                    break;
                default:
                    if (rightImage == null)
                    {
                        rightImage = leftImage.Clone();
                        ownsRightImage = true;
                    }
                    break;
            }

            if (leftImage.Empty() || rightImage.Empty())
                return null;

            var minHeight = Math.Min(leftImage.Rows, rightImage.Rows);
            var minWidth = Math.Min(leftImage.Cols, rightImage.Cols);
            using var resizedLeft = new Mat();
            using var resizedRight = new Mat();
            Cv2.Resize(leftImage, resizedLeft, new Size(minWidth, minHeight));
            Cv2.Resize(rightImage, resizedRight, new Size(minWidth, minHeight));

            var result = new Mat(Math.Max(resizedRight.Rows, resizedLeft.Rows),
                resizedRight.Cols + resizedLeft.Cols, resizedLeft.Type(), Scalar.All(0));
            resizedLeft.CopyTo(result[new Rect(0, 0, resizedLeft.Cols, resizedLeft.Rows)]);
            resizedRight.CopyTo(result[new Rect(resizedLeft.Cols, 0, resizedRight.Cols, resizedRight.Rows)]);

            if (leftIsNew)
            {
                LastLeftImage?.Dispose();
                LastLeftImage = resizedLeft.Clone();
            }
            if (rightIsNew)
            {
                LastRightImage?.Dispose();
                LastRightImage = resizedRight.Clone();
            }

            return result;
        }
        finally
        {
            if (ownsLeftImage)
                leftImage?.Dispose();
            if (ownsRightImage && !ReferenceEquals(leftImage, rightImage))
                rightImage?.Dispose();
        }
    }

    public void Dispose()
    {
        LeftCam?.Dispose();
        if (!ReferenceEquals(LeftCam, RightCam))
            RightCam?.Dispose();
        LastLeftImage?.Dispose();
        LastRightImage?.Dispose();
    }
}

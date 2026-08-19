using OpenCvSharp;

namespace Baballonia.FastCorruptionDetector;

public class CorruptionResult
{
    public bool LeftCorrupted { get; set; }
    public bool RightCorrupted { get; set; }
    public double LeftValue { get; set; }
    public double RightValue { get; set; }
    public double LeftThreshold { get; set; }
    public double RightThreshold { get; set; }
}

public class DetectionStats
{
    public int TotalFrames { get; set; }
    public int CorruptedLeft { get; set; }
    public int CorruptedRight { get; set; }
    public double CorruptionRateLeft { get; set; }
    public double CorruptionRateRight { get; set; }
    public double BaseThreshold { get; set; }
    public double CurrentThreshold { get; set; }
    public int ThresholdUpdates { get; set; }
    public bool AdaptiveEnabled { get; set; }
}

public class FastCorruptionDetector(
    double threshold = 0.022669,
    bool useAdaptive = true,
    int adaptationWindow = 100) : IDisposable
{
    private readonly double _baseThreshold = threshold;
    private double _currentThreshold = threshold;

    private readonly double[] _recentValues = new double[Math.Max(1, adaptationWindow)];
    private readonly double[] _sortBuffer = new double[Math.Max(1, adaptationWindow)];
    private int _recentCount;
    private int _recentIndex;
    private readonly Mat _grayScratch = new();
    private readonly Mat _rowMeans = new();

    private int _totalFrames;
    private int _detectedCorruptedLeft;
    private int _detectedCorruptedRight;
    private int _thresholdUpdates;

    private double CalculateRowPatternConsistency(Mat image)
    {
        var gray = image;
        if (image.Channels() == 3)
        {
            Cv2.CvtColor(image, _grayScratch, ColorConversionCodes.BGR2GRAY);
            gray = _grayScratch;
        }

        if (gray.Rows <= 1)
            return 0.0;

        // Reduce all columns at once instead of allocating a float image, one Mat header per row,
        // and several managed arrays. Dividing the byte means by 255 is equivalent to the former
        // full-image ConvertTo(..., 1/255) operation.
        Cv2.Reduce(gray, _rowMeans, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_64F);
        var means = _rowMeans.AsSpan<double>();
        var previous = means[0] / 255.0;
        double sum = 0.0;
        double sumSquares = 0.0;
        for (var row = 1; row < means.Length; row++)
        {
            var current = means[row] / 255.0;
            var difference = current - previous;
            sum += difference;
            sumSquares += difference * difference;
            previous = current;
        }

        var count = means.Length - 1;
        var mean = sum / count;
        var variance = Math.Max(0.0, sumSquares / count - mean * mean);
        return Math.Sqrt(variance);
    }

    private void UpdateAdaptiveThreshold(double value)
    {
        if (!useAdaptive)
            return;

        _recentValues[_recentIndex] = value;
        _recentIndex = (_recentIndex + 1) % _recentValues.Length;
        _recentCount = Math.Min(_recentCount + 1, _recentValues.Length);

        if (_recentCount < 20)
            return;

        Array.Copy(_recentValues, _sortBuffer, _recentCount);
        Array.Sort(_sortBuffer, 0, _recentCount);
        double median = _sortBuffer[_recentCount / 2];

        for (var i = 0; i < _recentCount; i++)
            _sortBuffer[i] = Math.Abs(_recentValues[i] - median);
        Array.Sort(_sortBuffer, 0, _recentCount);
        double mad = _sortBuffer[_recentCount / 2];

        double adaptiveThreshold = median + 3.0 * mad;

        double minThreshold = _baseThreshold * 0.5;
        double maxThreshold = _baseThreshold * 3.0;

        _currentThreshold = Math.Max(minThreshold, Math.Min(maxThreshold, adaptiveThreshold));
        _thresholdUpdates++;
    }

    public (bool isCorrupted, double metricValue, double thresholdUsed) IsCorrupted(Mat frame)
    {
        double metricValue = CalculateRowPatternConsistency(frame);

        UpdateAdaptiveThreshold(metricValue);

        bool isCorrupted = metricValue > _currentThreshold;

        return (isCorrupted, metricValue, _currentThreshold);
    }

    public CorruptionResult ProcessFramePair(Mat leftFrame, Mat rightFrame)
    {
        _totalFrames++;

        var (leftCorrupted, leftValue, leftThreshold) = IsCorrupted(leftFrame);
        var (rightCorrupted, rightValue, rightThreshold) = IsCorrupted(rightFrame);

        if (leftCorrupted)
            _detectedCorruptedLeft++;
        if (rightCorrupted)
            _detectedCorruptedRight++;

        return new CorruptionResult
        {
            LeftCorrupted = leftCorrupted,
            RightCorrupted = rightCorrupted,
            LeftValue = leftValue,
            RightValue = rightValue,
            LeftThreshold = leftThreshold,
            RightThreshold = rightThreshold
        };
    }

    public DetectionStats GetStats()
    {
        return new DetectionStats
        {
            TotalFrames = _totalFrames,
            CorruptedLeft = _detectedCorruptedLeft,
            CorruptedRight = _detectedCorruptedRight,
            CorruptionRateLeft = _detectedCorruptedLeft / (double)Math.Max(1, _totalFrames),
            CorruptionRateRight = _detectedCorruptedRight / (double)Math.Max(1, _totalFrames),
            BaseThreshold = _baseThreshold,
            CurrentThreshold = _currentThreshold,
            ThresholdUpdates = _thresholdUpdates,
            AdaptiveEnabled = useAdaptive
        };
    }

    public void Dispose()
    {
        _grayScratch.Dispose();
        _rowMeans.Dispose();
    }
}

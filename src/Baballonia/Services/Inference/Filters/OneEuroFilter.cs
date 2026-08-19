using System;
using System.Collections.Generic;

namespace Baballonia.Services.Inference.Filters;

public class OneEuroFilter : IFilter
{
    private readonly float _initialMinCutoff;
    private readonly float _initialBeta;
    private readonly Func<string, float>? _betaForKey;
    private readonly Func<string, bool>? _bypassForKey;
    private readonly Func<string, OrderedFloatMap, bool>? _freezeForKey;
    private readonly Func<string, OrderedFloatMap, float?>? _maximumDeltaForKey;
    private bool _isInitialized;

    private string[] _keys;
    private float[] _minCutoff;
    private float[] _beta;
    private bool[] _bypass;
    private bool[] _frozen;
    private int[] _resumeFrames;
    private float[] _dCutoff;
    private float[] _xPrev;
    private float[] _dxPrev;

    private float[] _dx;
    private float[] _dxHat;
    private float[] _cutoff;
    private float[] _xHat;

    // Reusable output map
    private OrderedFloatMap _output;
    // The input map we sized our state to; detects a model hot-swap.
    private OrderedFloatMap _source;
    private DateTime _tPrev;

    public OneEuroFilter(float minCutoff = 1.0f, float beta = 0.0f,
        Func<string, float>? betaForKey = null,
        Func<string, bool>? bypassForKey = null,
        Func<string, OrderedFloatMap, bool>? freezeForKey = null,
        Func<string, OrderedFloatMap, float?>? maximumDeltaForKey = null)
    {
        _initialMinCutoff = minCutoff;
        _initialBeta = beta;
        _betaForKey = betaForKey;
        _bypassForKey = bypassForKey;
        _freezeForKey = freezeForKey;
        _maximumDeltaForKey = maximumDeltaForKey;
        _isInitialized = false;
    }

    public OrderedFloatMap Filter(OrderedFloatMap x)
    {
        // (Re)init on first frame or after a model swap. State is sized to the first frame;
        // a shrunk output overruns it, a grown one gets truncated/mis-keyed.
        if (!_isInitialized || ShapeChanged(x))
        {
            Initialize(x);
            return _output; // Return the initial state on the first frame
        }

        DateTime now = DateTime.UtcNow;
        float elapsedTime = (float)(now - _tPrev).TotalSeconds;

        ReadOnlySpan<float> xSpan = x.ValuesSpan;
        Span<float> outSpan = _output.ValuesSpan;
        int length = _xPrev.Length;

        if (elapsedTime <= 0.0f)
        {
            ReadOnlySpan<float> src = xSpan.Slice(0, length);
            src.CopyTo(outSpan);
            src.CopyTo(_xPrev);
            return _output;
        }

        for (int i = 0; i < length; i++)
        {
            float val = xSpan[i];

            if (_bypass[i])
            {
                // Latency-sensitive eye signals have dedicated processing in the eye pipeline.
                // Passing them through avoids adding latency merely because the
                // slower Widen/Squint/Brow expressions need noise reduction.
                outSpan[i] = val;
                _xPrev[i] = val;
                _dxPrev[i] = 0.0f;
                continue;
            }

            // Gaze inferred from a covered pupil is meaningless. Holding the public output later
            // in the eye pipeline is not enough: if OneEuro consumes those hidden values, its
            // private state drifts behind the closed lid and is released as a large jump when the
            // eye opens. Freeze both value and velocity until the pupil is visible again.
            if (_freezeForKey?.Invoke(_keys[i], x) == true)
            {
                outSpan[i] = _xPrev[i];
                _dxPrev[i] = 0.0f;
                _frozen[i] = true;
                _resumeFrames[i] = 0;
                continue;
            }

            if (_frozen[i])
            {
                _frozen[i] = false;
                _resumeFrames[i] = 6;
                _dxPrev[i] = 0.0f;
            }

            if (_resumeFrames[i] > 0)
            {
                // Resume with a fixed moderate cutoff. Using the ordinary beta term here would
                // interpret the difference accumulated during occlusion as enormous eye speed and
                // effectively jump straight to the first newly visible estimate.
                const float reopeningCutoff = 3.0f;
                var rResume = MathF.Tau * Math.Max(_minCutoff[i], reopeningCutoff) * elapsedTime;
                var aResume = rResume / (rResume + 1.0f);
                var resumed = aResume * val + (1.0f - aResume) * _xPrev[i];
                _xPrev[i] = LimitDelta(_keys[i], x, _xPrev[i], resumed);
                _dxPrev[i] = 0.0f;
                outSpan[i] = _xPrev[i];
                _resumeFrames[i]--;
                continue;
            }

            _dx[i] = (val - _xPrev[i]) / elapsedTime;

            float r_d = MathF.Tau * _dCutoff[i] * elapsedTime;
            float a_d = r_d / (r_d + 1.0f);

            _dxHat[i] = a_d * _dx[i] + (1.0f - a_d) * _dxPrev[i];

            _cutoff[i] = _minCutoff[i] + _beta[i] * MathF.Abs(_dxHat[i]);

            float r = MathF.Tau * _cutoff[i] * elapsedTime;
            float a = r / (r + 1.0f);

            _xHat[i] = a * val + (1.0f - a) * _xPrev[i];

            _xPrev[i] = LimitDelta(_keys[i], x, _xPrev[i], _xHat[i]);
            _dxPrev[i] = _dxHat[i];

            outSpan[i] = _xPrev[i];
        }

        _tPrev = now;
        return _output;
    }

    private float LimitDelta(
        string key,
        OrderedFloatMap values,
        float current,
        float target)
    {
        var maximumDelta = _maximumDeltaForKey?.Invoke(key, values);
        if (!maximumDelta.HasValue ||
            !float.IsFinite(maximumDelta.Value) || maximumDelta.Value <= 0.0f ||
            MathF.Abs(target - current) <= maximumDelta.Value)
            return target;

        return current + MathF.CopySign(maximumDelta.Value, target - current);
    }

    // True when x's output set differs from ours. The runner reuses one map per model, so a
    // reference change flags a swap; length/keys confirm it so an equivalent map never resets us.
    private bool ShapeChanged(OrderedFloatMap x)
    {
        if (ReferenceEquals(x, _source))
            return false;

        if (x.Count != _keys.Length)
            return true;

        int i = 0;
        foreach (string key in x.Keys)
        {
            if (!string.Equals(key, _keys[i], StringComparison.Ordinal))
                return true;
            i++;
        }

        // Same shape, new instance — adopt it for the fast path.
        _source = x;
        return false;
    }

    private void Initialize(OrderedFloatMap x0)
    {
        int length = x0.Count;

        _keys = new string[length];
        _minCutoff = new float[length];
        _beta = new float[length];
        _bypass = new bool[length];
        _frozen = new bool[length];
        _resumeFrames = new int[length];
        _dCutoff = new float[length];
        _xPrev = new float[length];
        _dxPrev = new float[length];

        _dx = new float[length];
        _dxHat = new float[length];
        _cutoff = new float[length];
        _xHat = new float[length];

        int i = 0;
        foreach (KeyValuePair<string, float> kvp in x0)
        {
            _keys[i] = kvp.Key;
            _xPrev[i] = kvp.Value;
            _minCutoff[i] = _initialMinCutoff;
            _beta[i] = _betaForKey?.Invoke(kvp.Key) ?? _initialBeta;
            _bypass[i] = _bypassForKey?.Invoke(kvp.Key) ?? false;
            _dCutoff[i] = 1.0f;
            _dxPrev[i] = 0.0f;
            i++;
        }

        _output = new OrderedFloatMap(_keys);
        _source = x0;

        _xPrev.CopyTo(_output.ValuesSpan);

        _tPrev = DateTime.UtcNow;
        _isInitialized = true;
    }
}

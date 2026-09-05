using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace QuotaDock.Services;

internal sealed class SpringWindowAnimator(Window window)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _running;
    private double _targetLeft;
    private double _targetTop;
    private double _velocityX;
    private double _velocityY;
    private TimeSpan _lastFrame;

    public event EventHandler? Completed;
    public event EventHandler? Frame;
    public bool IsRunning => _running;

    public void Start(double targetLeft, double targetTop, double velocityX, double velocityY)
    {
        Stop();
        if (!double.IsFinite(targetLeft) || !double.IsFinite(targetTop))
        {
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _targetLeft = targetLeft;
        _targetTop = targetTop;
        _velocityX = ClampReleaseVelocity(velocityX, targetLeft - window.Left);
        _velocityY = ClampReleaseVelocity(velocityY, targetTop - window.Top);

        if (!SystemParameters.ClientAreaAnimation)
        {
            SetPosition(targetLeft, targetTop);
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _lastFrame = _clock.Elapsed;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!window.IsLoaded)
        {
            Stop();
            return;
        }

        try
        {
            var now = _clock.Elapsed;
            var delta = Math.Clamp((now - _lastFrame).TotalSeconds, 1d / 240d, 1d / 30d);
            _lastFrame = now;

            // Window repositioning must not overshoot a screen edge: damping 1.0, response 0.40 s.
            const double dampingRatio = 1.0;
            const double response = 0.40;
            var omega = 2 * Math.PI / response;

            Step(ref _velocityX, true, _targetLeft, omega, dampingRatio, delta);
            Step(ref _velocityY, false, _targetTop, omega, dampingRatio, delta);
            Frame?.Invoke(this, EventArgs.Empty);

            if (Math.Abs(window.Left - _targetLeft) < 0.2 && Math.Abs(window.Top - _targetTop) < 0.2 &&
                Math.Abs(_velocityX) < 2 && Math.Abs(_velocityY) < 2)
            {
                SetPosition(_targetLeft, _targetTop);
                Stop();
                Completed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch
        {
            Stop();
            try
            {
                SetPosition(_targetLeft, _targetTop);
                Completed?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                // Window may already be closing.
            }
        }
    }

    private void Step(ref double velocity, bool horizontal, double target, double omega, double dampingRatio, double delta)
    {
        var position = horizontal ? window.Left : window.Top;
        if (!double.IsFinite(position) || !double.IsFinite(velocity))
        {
            if (horizontal)
            {
                window.Left = target;
            }
            else
            {
                window.Top = target;
            }

            velocity = 0;
            return;
        }

        var acceleration = -omega * omega * (position - target) - 2 * dampingRatio * omega * velocity;
        velocity += acceleration * delta;
        position += velocity * delta;
        if (!double.IsFinite(position) || !double.IsFinite(velocity))
        {
            position = target;
            velocity = 0;
        }

        if (horizontal)
        {
            window.Left = position;
        }
        else
        {
            window.Top = position;
        }
    }

    private void SetPosition(double left, double top)
    {
        if (double.IsFinite(left))
        {
            window.Left = left;
        }

        if (double.IsFinite(top))
        {
            window.Top = top;
        }
    }

    private static double ClampReleaseVelocity(double velocity, double remainingDistance)
    {
        if (!double.IsFinite(velocity) || !double.IsFinite(remainingDistance))
        {
            return 0;
        }

        var directional = Math.Sign(remainingDistance) == Math.Sign(velocity) ? velocity : velocity * 0.25;
        var limit = Math.Min(1600, Math.Max(360, Math.Abs(remainingDistance) * 5));
        return Math.Clamp(directional, -limit, limit);
    }
}

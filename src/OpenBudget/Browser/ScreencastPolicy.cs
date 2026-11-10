namespace OpenBudget.Browser;

/// <summary>One JPEG frame of the remote page; Width/Height are the page size in CSS px (for mapping input coordinates).</summary>
public sealed record ScreencastFrame(byte[] Data, int Width, int Height, bool Still);

/// <summary>What the remote view sends when: frame size, quality, still timing (pure decisions).</summary>
public static class ScreencastPolicy
{
    public const int MovingQuality = 60;
    public const int StillQuality = 90;
    public const int MinWidth = 320;
    public static readonly TimeSpan StillDelay = TimeSpan.FromMilliseconds(300);
    /// <summary>Without input for this long, a changing page is shown as sharp stills only (no flicker).</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Screencast frame size for a canvas shown at <paramref name="displayCssWidth"/> CSS px: never larger than the viewport
    /// (Chromium does not upscale) and never larger than what the screen can show.
    /// </summary>
    public static (int Width, int Height) FrameSize(double displayCssWidth, double dpr, int viewportWidth, int viewportHeight)
    {
        if (double.IsNaN(dpr) || dpr < 1) dpr = 1;
        if (dpr > 3) dpr = 3;
        if (double.IsNaN(displayCssWidth) || displayCssWidth <= 0) displayCssWidth = viewportWidth;
        var w = (int)Math.Clamp(Math.Round(displayCssWidth * dpr), Math.Min(MinWidth, viewportWidth), viewportWidth);
        var h = (int)Math.Ceiling(w * (double)viewportHeight / viewportWidth);
        return (w, h);
    }

    /// <summary>Scale of the sharp still relative to the viewport (1 = CSS px, 2 = retina), 1..2.</summary>
    public static double StillScale(double displayCssWidth, double dpr, int viewportWidth)
    {
        if (double.IsNaN(dpr) || dpr < 1) dpr = 1;
        if (double.IsNaN(displayCssWidth) || displayCssWidth <= 0) return 1;
        return Math.Clamp(displayCssWidth * dpr / viewportWidth, 1, 2);
    }
}

/// <summary>Newest-wins hand-over: while the consumer is busy only the latest posted item is kept.</summary>
public sealed class LatestPump<T>(Func<T, Task> consume, Action<Exception>? onError = null) where T : class
{
    private readonly Lock _gate = new();
    private T? _pending;
    private bool _running;

    public void Post(T item)
    {
        lock (_gate)
        {
            _pending = item;
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            T item;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _running = false;
                    return;
                }
                item = _pending;
                _pending = null;
            }
            try { await consume(item); }
            catch (Exception ex) { onError?.Invoke(ex); }
        }
    }
}

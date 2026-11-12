namespace Zapretiks.App;

internal sealed class WheelNavigationGesture
{
    private const int Detent = 120;
    private static readonly TimeSpan InputGap = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(330);
    private TimeSpan? lastInput;
    private TimeSpan? lastNavigation;
    private int accumulatedDelta;

    public int ReadDirection(int delta, TimeSpan now)
    {
        if (delta == 0)
            return 0;

        if (lastNavigation is { } navigation && now - navigation < Cooldown)
        {
            ResetInput();
            return 0;
        }

        if ((lastInput is { } input && now - input > InputGap) || Math.Sign(accumulatedDelta) != Math.Sign(delta))
            accumulatedDelta = 0;

        lastInput = now;
        accumulatedDelta += Math.Clamp(delta, -Detent, Detent);
        if (Math.Abs(accumulatedDelta) < Detent)
            return 0;

        var direction = -Math.Sign(accumulatedDelta);
        ResetInput();
        return direction;
    }

    public void MarkNavigation(TimeSpan now)
    {
        lastNavigation = now;
        ResetInput();
    }

    public void ResetInput()
    {
        accumulatedDelta = 0;
        lastInput = null;
    }
}

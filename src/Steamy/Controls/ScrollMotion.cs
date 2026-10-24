namespace Steamy.Controls;

/// <summary>Frame-rate independent, critically damped motion with no per-step allocations.</summary>
internal sealed class ScrollMotion
{
    private const double Frequency = 22;
    internal double Position { get; private set; }
    internal double Velocity { get; private set; }
    internal double Target { get; private set; }
    internal bool IsResting => Position == Target && Velocity == 0;

    internal void Reset(double position) { Position = Target = position; Velocity = 0; }
    internal void Retarget(double target) => Target = target;
    internal void Advance(double seconds)
    {
        // Do not turn a delayed UI frame into a large catch-up jump.
        var step = Math.Clamp(seconds, 0, .05);
        if (step == 0 || IsResting) return;
        var distance = Position - Target;
        var slope = Velocity + Frequency * distance;
        var decay = Math.Exp(-Frequency * step);
        var next = Target + (distance + slope * step) * decay;
        Velocity = (Velocity - Frequency * slope * step) * decay;
        Position = next;
        if (Math.Sign(next - Target) != Math.Sign(distance) ||
            Math.Abs(next - Target) <= .15 && Math.Abs(Velocity) <= 3)
        { Position = Target; Velocity = 0; }
    }
}

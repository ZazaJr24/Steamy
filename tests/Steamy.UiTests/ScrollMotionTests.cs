using Steamy.Controls;

namespace Steamy.UiTests;

public sealed class ScrollMotionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void CommittedOffsetsStayOnPhysicalPixelsWithoutLosingTheEndOfThePage(double dpi)
    {
        for (var offset = 0d; offset < 99; offset += .13)
        {
            var pixels = SmoothScroll.SnapOffset(offset, 100.1, dpi) * dpi;
            Assert.Equal(Math.Round(pixels), pixels, 8);
        }
        Assert.Equal(100.1, SmoothScroll.SnapOffset(100.1, 100.1, dpi));
        Assert.Equal(100.1, SmoothScroll.SnapOffset(500, 100.1, dpi));
        Assert.Equal(0, SmoothScroll.SnapOffset(-1, 100.1, dpi));
    }
    [Fact]
    public void EqualElapsedTimeProducesTheSamePositionAcrossRefreshRates()
    {
        var slow = new ScrollMotion(); var fast = new ScrollMotion();
        slow.Reset(40); fast.Reset(40); slow.Retarget(900); fast.Retarget(900);
        for (var frame = 0; frame < 6; frame++) slow.Advance(1d / 60);
        for (var frame = 0; frame < 12; frame++) fast.Advance(1d / 120);
        Assert.Equal(slow.Position, fast.Position, 8);
        Assert.Equal(slow.Velocity, fast.Velocity, 8);
    }

    [Fact]
    public void MoreWheelInputPreservesVelocityRatherThanRestartingAnEase()
    {
        var motion = new ScrollMotion(); motion.Reset(300); motion.Retarget(354);
        motion.Advance(1d / 60); var velocity = motion.Velocity; var position = motion.Position;
        motion.Retarget(408);
        Assert.Equal(velocity, motion.Velocity); Assert.Equal(position, motion.Position);
        motion.Advance(1d / 60);
        Assert.True(motion.Velocity > velocity); Assert.True(motion.Position > position);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(1000, 0)]
    public void MotionSettlesWithoutOvershooting(double start, double target)
    {
        var motion = new ScrollMotion(); motion.Reset(start); motion.Retarget(target);
        var previous = start;
        for (var frame = 0; frame < 120; frame++)
        {
            motion.Advance(1d / 120);
            Assert.InRange(motion.Position, Math.Min(start, target), Math.Max(start, target));
            Assert.True(target > start ? motion.Position >= previous : motion.Position <= previous);
            previous = motion.Position;
        }
        Assert.True(motion.IsResting); Assert.Equal(target, motion.Position);
    }

    [Fact]
    public void ADelayedFrameDoesNotSkipToTheEnd()
    {
        var delayed = new ScrollMotion(); var normal = new ScrollMotion();
        delayed.Reset(0); normal.Reset(0); delayed.Retarget(500); normal.Retarget(500);
        delayed.Advance(.8); normal.Advance(.05);
        Assert.Equal(normal.Position, delayed.Position);
        Assert.InRange(delayed.Position, 1, 499);
    }

    [Fact]
    public void RetargetingAndAdvancingDoesNotAllocatePerFrame()
    {
        var motion = new ScrollMotion(); motion.Reset(0); motion.Retarget(500); motion.Advance(.01);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) { motion.Retarget(i); motion.Advance(1d / 120); }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}

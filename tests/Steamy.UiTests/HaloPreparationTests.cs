using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Steamy.Controls;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class HaloPreparationTests
{
    [Fact]
    public void PreparedHaloUsesFrozenPaintAndDropsItOnUnload()
    {
        OnSta(() =>
        {
            var halo=new ArtworkHalo {Source=Artwork(),IsActive=true,Width=280,Height=160};
            halo.Measure(new Size(280,160)); halo.Arrange(new Rect(0,0,280,160));
            // A windowless element is not IsVisible. Prepare the production paint
            // phase directly; this is not a claim about real hover/window events.
            var lights=ArtworkHalo.PrepareLightsAsync(halo.Source!,1.75,1,default).GetAwaiter().GetResult();
            typeof(ArtworkHalo).GetField("_lights",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(halo,lights);
            try
            {
                Assert.Equal(12,Lights(halo)!.Length);
                Assert.All(Lights(halo)!,brush=>Assert.True(brush.IsFrozen));
                var visual=new DrawingVisual();
                using(var dc=visual.RenderOpen()) typeof(ArtworkHalo).GetMethod("OnRender",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(halo,[dc]);
                Assert.False(VisualTreeHelper.GetDrawing(visual).Bounds.IsEmpty);
            }
            finally { halo.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent)); }
            Assert.Null(Lights(halo)); Assert.Equal(0,halo.Opacity);
        });
    }

    [Fact]
    public void CancelledHaloPreparationDoesNotSampleAndAnIndependentReplacementStillCompletes()
    {
        OnSta(() =>
        {
            using var blocked=new BlockedWorker();
            var first=Artwork(); var second=Artwork();
            using var cancellation=new CancellationTokenSource();
            var abandoned=ArtworkHalo.PrepareLightsAsync(first,1.75,1,cancellation.Token);
            var replacement=ArtworkHalo.PrepareLightsAsync(second,1.75,.24,default);
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(()=>abandoned.GetAwaiter().GetResult());
            blocked.Release();
            var lights=replacement.GetAwaiter().GetResult();
            Assert.Equal(12,lights.Length); Assert.All(lights,brush=>Assert.True(brush.IsFrozen));
            Assert.False(ArtworkColorSample.TryLuminance(first,.5,.5,1.75,out _));
            Assert.True(ArtworkColorSample.TryLuminance(second,.5,.5,1.75,out _));
        });
    }

    private static Brush[]? Lights(ArtworkHalo halo)=>(Brush[]?)typeof(ArtworkHalo).GetField("_lights",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(halo);
    private static void PumpUntil(Func<bool> condition)
    {
        var frame=new DispatcherFrame(); var until=DateTime.UtcNow.AddSeconds(5);
        var timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(10)};
        timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=until) frame.Continue=false;};
        timer.Start(); try {Dispatcher.PushFrame(frame);} finally {timer.Stop();}
        Assert.True(condition(),"Windowless halo preparation timed out.");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawingAnUnpreparedHaloDoesNotReadBackItsSourcePixels(bool active)
    {
        OnSta(() =>
        {
            var source=Artwork();
            var halo=new ArtworkHalo {Source=source,IsActive=active,Width=280,Height=160};
            halo.Measure(new Size(280,160)); halo.Arrange(new Rect(0,0,280,160));
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
                typeof(ArtworkHalo).GetMethod("OnRender",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(halo,[dc]);
            Assert.False(ArtworkColorSample.TryLuminance(source,.5,.5,1.75,out _),"A draw pass must never create a source sample, even when the halo is initially empty.");
        });
    }

    [Fact]
    public void AColdColorSampleWaitsForTheWorkerButCachedContrastStaysImmediate()
    {
        OnSta(() =>
        {
            var source=Artwork();
            using var blocked=new BlockedWorker();
            var pending=ArtworkColorSample.ForAsync(source,default);
            try
            {
                Assert.False(pending.IsCompleted);
                Assert.False(ArtworkColorSample.TryLuminance(source,.5,.5,1.75,out _));
            }
            finally { blocked.Release(); }
            var sample=pending.GetAwaiter().GetResult();
            Assert.True(ArtworkColorSample.TryLuminance(source,.5,.5,1.75,out var luminance));
            Assert.Equal(UiThemeService.Luminance(ArtworkColorSample.ColorAt(source,.5,.5,1.75)),luminance);
            var cached=ArtworkColorSample.ForAsync(source,default);
            Assert.True(cached.IsCompletedSuccessfully); Assert.Same(sample,cached.Result);
        });
    }

    [Fact]
    public void ACancelledColorConsumerDoesNotPreventAnotherConsumerFromPreparing()
    {
        OnSta(() =>
        {
            var source=Artwork();
            using var blocked=new BlockedWorker();
            using var cancellation=new CancellationTokenSource();
            var abandoned=ArtworkColorSample.ForAsync(source,cancellation.Token);
            var current=ArtworkColorSample.ForAsync(source,default);
            try
            {
                cancellation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => abandoned.GetAwaiter().GetResult());
                Assert.False(current.IsCompleted);
            }
            finally { blocked.Release(); }
            _=current.GetAwaiter().GetResult();
            Assert.True(ArtworkColorSample.TryLuminance(source,.5,.5,1.75,out _));
        });
    }

    [Theory]
    [InlineData(.7)]
    [InlineData(1.75)]
    [InlineData(4)]
    public void BackgroundSamplesPreserveThePreviousCropAndColors(double aspect)
    {
        OnSta(() =>
        {
            var source=Artwork();
            var reference=source.CloneCurrentValue(); reference.Freeze();
            _=ArtworkColorSample.ForAsync(source,default).GetAwaiter().GetResult();
            foreach(var point in new[]{new Point(.04,.5),new Point(.06,.08),new Point(.94,.92),new Point(.96,.5)})
            {
                var expected=UiThemeService.Luminance(ArtworkColorSample.ColorAt(reference,point.X,point.Y,aspect));
                Assert.True(ArtworkColorSample.TryLuminance(source,point.X,point.Y,aspect,out var actual));
                Assert.Equal(expected,actual);
            }
        });
    }

    [Fact]
    public void CompletedColorSamplesDoNotKeepTheArtworkAlive()
    {
        OnSta(() =>
        {
            var weak=SampleAndRelease();
            _=ArtworkRenderWorker.RunAsync(()=>0,default).GetAwaiter().GetResult();
            for(var i=0;i<3 && weak.IsAlive;i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            Assert.False(weak.IsAlive);
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SampleAndRelease()
    { var source=Artwork(); _=ArtworkColorSample.ForAsync(source,default).GetAwaiter().GetResult(); return new WeakReference(source); }
    private static DrawingImage Artwork()
    {
        var brush=new LinearGradientBrush(Colors.CornflowerBlue,Colors.OrangeRed,0);
        var image=new DrawingImage(new GeometryDrawing(brush,null,new RectangleGeometry(new Rect(0,0,320,160)))); image.Freeze(); return image;
    }
    private sealed class BlockedWorker : IDisposable
    {
        private readonly ManualResetEventSlim _entered=new(),_release=new();
        private readonly Task<int> _work;
        internal BlockedWorker()
        {
            _work=ArtworkRenderWorker.RunAsync(()=>{_entered.Set(); if(!_release.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException(); return 0;},default);
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(5)));
        }
        internal void Release()=>_release.Set();
        public void Dispose() {Release(); _work.GetAwaiter().GetResult(); _entered.Dispose(); _release.Dispose();}
    }
    private static void OnSta(Action check)
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try{check();}catch(Exception error){failure=error;}
            finally {SynchronizationContext.SetSynchronizationContext(null); dispatcher.InvokeShutdown();}
        }){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if(failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

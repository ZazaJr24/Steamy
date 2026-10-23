using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class BrushResourcesTests
{
    [Fact]
    public void UnchangedPaletteDoesNotInvalidateDynamicConsumersButARealChangeDoes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new Grid();
                var caption = new TextBlock { Text = "Release date" };
                root.Children.Add(caption);
                BrushResources.SetColor(root.Resources, "Caption", Colors.White);
                caption.SetResourceReference(TextBlock.ForegroundProperty, "Caption");
                root.Measure(new Size(300, 100)); root.Arrange(new Rect(0, 0, 300, 100));
                var original = caption.Foreground;
                var changes = 0;
                var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.ForegroundProperty, typeof(TextBlock))!;
                EventHandler handler = (_, _) => changes++;
                descriptor.AddValueChanged(caption, handler);
                try
                {
                    for (var imageArrival = 0; imageArrival < 100; imageArrival++)
                    {
                        BrushResources.SetColor(root.Resources, "Caption", Colors.White);
                        var equivalent = new SolidColorBrush(Colors.White); equivalent.Freeze();
                        BrushResources.Set(root.Resources, "Caption", equivalent);
                        Assert.Same(original, caption.Foreground);
                    }
                    Assert.Equal(0, changes);
                    BrushResources.SetColor(root.Resources, "Caption", Colors.Black);
                    Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(caption.Foreground).Color);
                    Assert.Equal(1, changes);
                    var translated = new SolidColorBrush(Colors.Black) { Transform = new TranslateTransform(1, 0) };
                    translated.Freeze();
                    BrushResources.Set(root.Resources, "Caption", translated);
                    Assert.Same(translated, caption.Foreground);
                }
                finally { descriptor.RemoveValueChanged(caption, handler); }
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Palette consumer check timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

using System;
using System.Windows;
using System.Windows.Media;

namespace Steamy.Setup
{
    // Native resolution vector light fields. The animation redraws this visual only;
    // labels/buttons are never blurred, scaled from a bitmap or raster-cached.
    sealed class FluidBackdrop : FrameworkElement
    {
        public double Phase;
        public bool HighContrast;
        static Color Rgb(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static Brush Light(Color color, double opacity)
        {
            var brush = new RadialGradientBrush { Center = new Point(.5, .5), GradientOrigin = new Point(.5, .5), RadiusX = .5, RadiusY = .5, Opacity = opacity };
            brush.GradientStops.Add(new GradientStop(color, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(130, color.R, color.G, color.B), .43));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            return brush;
        }
        protected override void OnRender(DrawingContext dc)
        {
            var w = ActualWidth; var h = ActualHeight; if (w == 0 || h == 0) return;
            dc.DrawRectangle(new SolidColorBrush(HighContrast ? Colors.Black : Rgb("#101016")), null, new Rect(0, 0, w, h));
            if (HighContrast) return;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            var x = Math.Sin(Phase * .72); var y = Math.Cos(Phase * .53);
            dc.DrawEllipse(Light(Rgb("#626ADD"), .29), null, new Point(w * (.13 + x * .12), h * (.63 + y * .16)), w * .69, h * .75);
            dc.DrawEllipse(Light(Rgb("#9F57DA"), .30), null, new Point(w * (.92 - x * .11), h * (.89 - y * .13)), w * .68, h * .70);
            dc.DrawEllipse(Light(Rgb("#A97CA5"), .16), null, new Point(w * (.42 + Math.Cos(Phase * .43) * .32), h * 1.24), w * .49, h * .60);
            dc.DrawEllipse(Light(Rgb("#536495"), .16), null, new Point(w * 1.12, h * (-.18 + y * .1)), w * .73, h * .76);
            // A slow ribbon passing along the lower edge, with a broad diffused body
            // and a restrained highlight. Its cubic geometry changes continuously.
            var ribbon = new StreamGeometry();
            using (var shape = ribbon.Open())
            {
                shape.BeginFigure(new Point(-.2 * w, (1.03 + .10 * x) * h), true, true);
                shape.BezierTo(new Point(.14 * w, (.45 + .15 * y) * h), new Point(.67 * w, (1.48 - .17 * x) * h), new Point(1.18 * w, (.60 + .14 * y) * h), true, false);
                shape.LineTo(new Point(1.20 * w, 1.08 * h), true, false);
                shape.BezierTo(new Point(.75 * w, (1.64 - .15 * x) * h), new Point(.10 * w, (.59 + .17 * y) * h), new Point(-.2 * w, 1.40 * h), true, false);
            }
            var tint = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(.3, 1) };
            tint.GradientStops.Add(new GradientStop(Rgb("#004C4B7C"), 0));
            tint.GradientStops.Add(new GradientStop(Rgb("#264E599D"), .4));
            tint.GradientStops.Add(new GradientStop(Rgb("#21956AAE"), .76));
            tint.GradientStops.Add(new GradientStop(Rgb("#00875399"), 1));
            dc.DrawGeometry(tint, null, ribbon);
            // The dark centre keeps type quiet while light moves around it.
            dc.DrawEllipse(Light(Rgb("#101016"), .62), null, new Point(w * .50, h * .36), w * .53, h * .55);
            dc.Pop();
        }
    }
}

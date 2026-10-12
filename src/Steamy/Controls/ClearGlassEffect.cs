using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Steamy.Controls;

/// <summary>Single-pass capsule edge refraction without chromatic splitting.</summary>
public sealed class ClearGlassEffect : ShaderEffect
{
    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty(nameof(Input), typeof(ClearGlassEffect), 0);
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(Point), typeof(ClearGlassEffect), new UIPropertyMetadata(new Point(520, 34), PixelShaderConstantCallback(0)));
    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Point Size { get => (Point)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public ClearGlassEffect()
    {
        PixelShader = new PixelShader { UriSource = new Uri("pack://application:,,,/Steamy;component/Resources/LiquidGlass/ClearGlass.ps") };
        UpdateShaderValue(InputProperty); UpdateShaderValue(SizeProperty);
    }
}

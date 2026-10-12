using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Steamy.Controls;

/// <summary>Centers a proportional video crop inside a fixed poster viewport.</summary>
internal sealed class ViewportMediaElement : MediaElement
{
    // MediaElement's native measure/arrange pipeline prepares the video surface.
    // TrailerPreview supplies the fixed viewport and UniformToFill already crops it.
    // Bypassing that pipeline can leave the video blank or apply the crop twice.
}

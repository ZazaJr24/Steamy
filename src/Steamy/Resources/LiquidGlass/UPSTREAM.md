# WPF Liquid Glass Effect

Source: https://github.com/dragosniamtu/WPF-Liquid-Glass-Effect

Pinned commit: `ded012c90fd646546267fbeb2b69fef3ed89dcb1` (original MIT WPF prototype).

The compiled `DemoApp/Shaders/GlassyEffect.ps` is preserved unchanged.
SHA-256: `2B34EEE9FBFE62408F22708EBC2985D3DD0E0D64718FD81B4ACA921E33DFC7A6`.
The companion `GlassyEffect.cs` is adapted to the Steamy namespace and resource URI.

Steamy feeds its existing game-artwork background to the effect rather than capturing
the desktop. The shader is applied only to glass background layers, leaving foreground
text, icons and controls untouched. Devices without pixel shader 3.0 use the cached
blurred background. Reduced effects and high contrast use opaque surfaces.

This integration does not depend on the newer proprietary XamlTemplates.FX package.
The upstream MIT license is reproduced in LICENSE.txt; its original attribution
notice is retained in UPSTREAM-NOTICES.md.

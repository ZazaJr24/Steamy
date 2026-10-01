using System.Windows.Input;

namespace Steamy.Controls;

internal static class ScrollNavigation
{
    internal static bool IsNavigationKeyDown() => Keyboard.IsKeyDown(Key.Tab)
        || Keyboard.IsKeyDown(Key.Up) || Keyboard.IsKeyDown(Key.Down)
        || Keyboard.IsKeyDown(Key.Left) || Keyboard.IsKeyDown(Key.Right)
        || Keyboard.IsKeyDown(Key.PageUp) || Keyboard.IsKeyDown(Key.PageDown)
        || Keyboard.IsKeyDown(Key.Home) || Keyboard.IsKeyDown(Key.End);
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Steamy.ControllerProtocol;
using Steamy.Pages;
using Steamy.Services;

namespace Steamy.Controls;

internal enum GamepadAction { None, Up, Down, Left, Right, Accept, Back, Keyboard, PreviousPage, NextPage }
internal sealed class GamepadRepeat
{
    private uint _previous;
    private GamepadAction _direction;
    private long _next;
    private bool _neutral;
    public GamepadAction Feed(ControllerInput? state, long now)
    {
        if (state is null) { _previous = 0; _direction = GamepadAction.None; _neutral = false; return GamepadAction.None; }
        var direction = (state.Buttons & 1) != 0 || state.LY > .55 ? GamepadAction.Up : (state.Buttons & 2) != 0 || state.LY < -.55 ? GamepadAction.Down
            : (state.Buttons & 4) != 0 || state.LX < -.55 ? GamepadAction.Left : (state.Buttons & 8) != 0 || state.LX > .55 ? GamepadAction.Right : GamepadAction.None;
        var pressed = state.Buttons & ~_previous; _previous = state.Buttons;
        if (!_neutral) { _neutral = state.Buttons == 0 && direction == GamepadAction.None; return GamepadAction.None; }
        if ((pressed & 0x1000) != 0) return GamepadAction.Accept;
        if ((pressed & 0x2000) != 0) return GamepadAction.Back;
        if ((pressed & 0x4000) != 0) return GamepadAction.Keyboard;
        if ((pressed & 0x100) != 0) return GamepadAction.PreviousPage;
        if ((pressed & 0x200) != 0) return GamepadAction.NextPage;
        if (direction == GamepadAction.None) { _direction = direction; return GamepadAction.None; }
        if (direction != _direction) { _direction = direction; _next = now + 340; return direction; }
        if (now < _next) return GamepadAction.None;
        _next = now + 95; return direction;
    }
}
internal sealed class GamepadNavigation : IDisposable
{
    private readonly MainWindow _window;
    private readonly IControllerBackend _backend;
    private readonly ISettingsService _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly GamepadRepeat _repeat = new();
    private readonly ControllerKeyboard _keyboard;
    private readonly Border _hints;
    private volatile bool _active, _enabled;
    private bool _controllerFocus;
    private Adorner? _highlight;
    private AdornerLayer? _highlightLayer;
    private readonly System.Windows.Threading.DispatcherTimer _hintTimer;
    public GamepadNavigation(MainWindow window, IControllerBackend backend, ISettingsService settings, bool pollHardware = true)
    {
        _window = window; _backend = backend; _settings = settings; _active = window.IsActive && window.WindowState != WindowState.Minimized; _enabled = settings.Load().ControllerNavigation;
        _keyboard = new(window);
        _hints = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 26, 29, 37)), CornerRadius = new(10), Padding = new(12, 7, 12, 7), Margin = new(24), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, IsHitTestVisible = false, Child = new TextBlock { Text = "A / Cross  Select     B / Circle  Back     LB / RB  Pages     X / Square  Keyboard", Foreground = Brushes.White, FontSize = 11 } };
        var root = (Grid)window.Content; Grid.SetRowSpan(_hints, 3); Panel.SetZIndex(_hints, 80); root.Children.Add(_hints);
        _hintTimer = new() { Interval = TimeSpan.FromSeconds(5) }; _hintTimer.Tick += HideHints;
        window.Activated += Activated; window.Deactivated += Deactivated; window.StateChanged += Activated; window.PreviewMouseDown += MouseInput; window.PreviewKeyDown += KeyInput;
        window.PreviewGotKeyboardFocus += FocusChanged;
        if (pollHardware) _ = Task.Run(PollAsync);
    }
    private void Activated(object? sender, EventArgs args) { _active = _window.IsActive && _window.WindowState != WindowState.Minimized; _enabled = _settings.Load().ControllerNavigation; }
    private void Deactivated(object? sender, EventArgs args) { _active = false; }
    internal void RefreshEnabled() => _enabled = _settings.Load().ControllerNavigation;
    private async Task PollAsync()
    {
        var noXinput = false;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_active && _enabled ? 25 : 200, _lifetime.Token);
                if (!_active || !_enabled) { _repeat.Feed(null, 0); continue; }
                // Sony input is authoritative while the mapper is active; don't also consume its virtual Xbox output.
                var state = _backend.State.Devices.FirstOrDefault()?.Input;
                if (state is null && !noXinput)
                {
                    try { state = ReadXinput(); } catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { noXinput = true; }
                }
                var action = _repeat.Feed(state, Environment.TickCount64);
                if (action != GamepadAction.None) await _window.Dispatcher.InvokeAsync(() => { if (_active && _settings.Load().ControllerNavigation) Perform(action); });
            }
            catch (OperationCanceledException) { break; }
        }
    }
    internal void Perform(GamepadAction action)
    {
        _controllerFocus = true; _hints.Visibility = Visibility.Visible; _hintTimer.Stop(); _hintTimer.Start();
        var focus = Keyboard.FocusedElement as FrameworkElement;
        if (action == GamepadAction.Back)
        {
            if (_keyboard.IsOpen) _keyboard.Close();
            else if (focus is ComboBox { IsDropDownOpen: true } combo) combo.IsDropDownOpen = false;
            else if (_window.RootNavigationView.IsPaneOpen) _window.RootNavigationView.IsPaneOpen = false;
            else if (Descendant<FriendsPage>(_window)?.CloseTransientPanels() == true) { }
            else if (PresentationSource.FromVisual(_window) is { } source)
            {
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                (focus as UIElement ?? _window).RaiseEvent(key);
                if (!key.Handled && _window.RootNavigationView.CanGoBack) _window.RootNavigationView.GoBack();
            }
            return;
        }
        if (action == GamepadAction.Keyboard || action == GamepadAction.Accept && focus is TextBox)
        { if (focus is TextBox text) _keyboard.Open(text); return; }
        if (action is GamepadAction.NextPage or GamepadAction.PreviousPage && !_keyboard.IsOpen)
        {
            var pages = new[] { typeof(DashboardPage), typeof(LibraryPage), typeof(DownloadsPage), typeof(FriendsPage), typeof(ControllersPage), typeof(SettingsPage) }.Where(page => Services.DiscordIntegration.Enabled || page != typeof(FriendsPage)).ToArray();
            var page = _window.IsSocialDockOpen && _window.SocialDockPage?.IsKeyboardFocusWithin == true ? _window.SocialDockPage : Descendant<Page>(_window);
            var index = Array.IndexOf(pages, page?.GetType());
            _window.NavigateWithFeedback(pages[(index + (action == GamepadAction.NextPage ? 1 : pages.Length - 1) + pages.Length) % pages.Length]); return;
        }
        if (action is GamepadAction.NextPage or GamepadAction.PreviousPage) return;
        if (action == GamepadAction.Accept)
        {
            if (focus is ComboBox combo) { combo.IsDropDownOpen = !combo.IsDropDownOpen; return; }
            if (focus is Wpf.Ui.Controls.NavigationViewItem { TargetPageType: { } route }) { _window.RootNavigationView.Navigate(route); return; }
            if (focus is UIElement element)
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle) toggle.Toggle();
            }
            return;
        }
        if (focus is ComboBox { IsDropDownOpen: true } openCombo)
        { openCombo.SelectedIndex = Math.Clamp(openCombo.SelectedIndex + (action is GamepadAction.Up or GamepadAction.Left ? -1 : 1), 0, Math.Max(0, openCombo.Items.Count - 1)); return; }
        if (focus is Slider slider && action is GamepadAction.Left or GamepadAction.Right)
        { slider.Value = Math.Clamp(slider.Value + (action == GamepadAction.Left ? -1 : 1) * Math.Max(1, slider.SmallChange), slider.Minimum, slider.Maximum); return; }
        var direction = action switch { GamepadAction.Up => FocusNavigationDirection.Up, GamepadAction.Down => FocusNavigationDirection.Down, GamepadAction.Left => FocusNavigationDirection.Left, _ => FocusNavigationDirection.Right };
        if (focus is null || !focus.IsVisible) { _window.MoveFocus(new(FocusNavigationDirection.First)); return; }
        if (!focus.MoveFocus(new(direction))) focus.MoveFocus(new(action is GamepadAction.Up or GamepadAction.Left ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        if (Keyboard.FocusedElement is FrameworkElement selected) selected.BringIntoView();
        Highlight(Keyboard.FocusedElement as UIElement);
    }
    private void FocusChanged(object sender, KeyboardFocusChangedEventArgs args) { if (_controllerFocus) Highlight(args.NewFocus as UIElement); }
    private void Highlight(UIElement? target)
    {
        if (_highlight is not null) _highlightLayer?.Remove(_highlight); _highlight = null;
        if (target is null || AdornerLayer.GetAdornerLayer(target) is not { } layer) return;
        _highlightLayer = layer; _highlight = new GamepadFocusAdorner(target); layer.Add(_highlight);
    }
    private void MouseInput(object sender, MouseButtonEventArgs args) => HideHints(null, EventArgs.Empty);
    private void KeyInput(object sender, KeyEventArgs args) => HideHints(null, EventArgs.Empty);
    private void HideHints(object? sender, EventArgs args) { _hintTimer.Stop(); _hints.Visibility = Visibility.Collapsed; _controllerFocus = false; Highlight(null); }
    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) return match; if (Descendant<T>(child) is { } nested) return nested; } return null; }
    [StructLayout(LayoutKind.Sequential)] private struct Pad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Packet; public Pad Pad; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out State state);
    private static ControllerInput? ReadXinput()
    { for (uint slot = 0; slot < 4; slot++) if (XInputGetState(slot, out var state) == 0) { var pad = state.Pad; return new(pad.LX / 32768f, pad.LY / 32768f, pad.RX / 32768f, pad.RY / 32768f, pad.LT / 255f, pad.RT / 255f, pad.Buttons); } return null; }
    public void Dispose()
    {
        _lifetime.Cancel(); _hintTimer.Stop(); Highlight(null); _keyboard.Close();
        _window.Activated -= Activated; _window.Deactivated -= Deactivated; _window.StateChanged -= Activated; _window.PreviewMouseDown -= MouseInput; _window.PreviewKeyDown -= KeyInput; _window.PreviewGotKeyboardFocus -= FocusChanged;
        ((Grid)_window.Content).Children.Remove(_hints);
    }
}
internal sealed class GamepadFocusAdorner(UIElement target) : Adorner(target)
{
    protected override void OnRender(DrawingContext context)
    { IsHitTestVisible = false; context.DrawRoundedRectangle(null, new Pen(SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(Color.FromRgb(140, 150, 255)), 2), new Rect(new Point(-2, -2), new Size(AdornedElement.RenderSize.Width + 4, AdornedElement.RenderSize.Height + 4)), 6, 6); }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Steamy.Controls;

internal sealed class ControllerKeyboard(MainWindow window)
{
    private Border? _overlay;
    private TextBox? _target;
    private bool _shift;
    private bool _symbols;
    private readonly List<Button> _keys = [];
    private static readonly string[] Letters = ["1234567890", "qwertyuiop", "asdfghjklö", "zxcvbnmäüß"];
    private static readonly string[] Symbols = ["!@#$%^&*()", "-_=+[]{}<>", ";:'\"\\|/?.,", "€£¥§°`~äöü"];
    public bool IsOpen => _overlay is not null;
    public void Open(TextBox target)
    {
        Close(); _target = target;
        _overlay = new Border { Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var card = new Border { Width = Math.Min(700, window.ActualWidth - 64), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(24), Padding = new(20), CornerRadius = new(18) };
        card.SetResourceReference(Border.BackgroundProperty, "SocialNotificationBrush");
        var layout = new StackPanel(); layout.Children.Add(new TextBlock { Text = "Keyboard · X / Square", FontSize = 18, Margin = new(0, 0, 0, 12) });
        _keys.Clear();
        foreach (var keys in Letters)
        {
            var row = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = keys.Length };
            foreach (var key in keys)
            {
                var button = new Button { Content = key.ToString(), MinHeight = 44, Margin = new(3), Padding = new(6) };
                button.Click += (_, _) => Insert((string)button.Content); row.Children.Add(button); _keys.Add(button);
            }
            layout.Children.Add(row);
        }
        RefreshKeys();
        var actions = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 6 };
        AddKey(actions, "Shift", () => { _shift = !_shift; RefreshKeys(); }); AddKey(actions, "Symbols", () => { _symbols = !_symbols; RefreshKeys(); }); AddKey(actions, "Space", () => Insert(" "));
        AddKey(actions, "⌫", () => { if (_target?.IsReadOnly == false) System.Windows.Documents.EditingCommands.Backspace.Execute(null, _target); });
        AddKey(actions, "New line", () => { if (_target?.AcceptsReturn == true) Insert("\n"); }); AddKey(actions, "Done", Close);
        layout.Children.Add(actions); card.Child = layout; _overlay.Child = card;
        Grid.SetRowSpan(_overlay, 3); Panel.SetZIndex(_overlay, 100); ((Grid)window.Content).Children.Add(_overlay);
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle); KeyboardNavigation.SetDirectionalNavigation(card, KeyboardNavigationMode.Cycle);
        ((Button)((Panel)layout.Children[1]).Children[0]).Focus();
    }
    private static void AddKey(Panel row, string label, Action action)
    { var button = new Button { Content = label, MinHeight = 44, Margin = new(3), Padding = new(6) }; button.Click += (_, _) => action(); row.Children.Add(button); }
    private void Insert(string value)
    { if (_target is null || _target.IsReadOnly || _target.MaxLength > 0 && _target.Text.Length - _target.SelectionLength + value.Length > _target.MaxLength) return; var start = _target.SelectionStart; _target.SelectedText = value; _target.Select(start + value.Length, 0); }
    public void Close()
    { if (_overlay is not null) ((Grid)window.Content).Children.Remove(_overlay); _overlay = null; _keys.Clear(); if (_target?.IsVisible == true) _target.Focus(); _target = null; }
    private void RefreshKeys()
    {
        var keys = string.Concat(_symbols ? Symbols : Letters);
        for (var index = 0; index < _keys.Count; index++) _keys[index].Content = (_shift ? char.ToUpperInvariant(keys[index]) : keys[index]).ToString();
    }
}

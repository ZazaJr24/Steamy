using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.Controls;

public partial class BackgroundOptions : UserControl
{
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(nameof(Settings), typeof(AppSettings), typeof(BackgroundOptions), new PropertyMetadata(null, SettingsChanged));
    public AppSettings? Settings { get => (AppSettings?)GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    private bool _updating;
    private AppSettings? _observed;
    public BackgroundOptions()
    {
        InitializeComponent();
        Loaded += (_, _) => Observe(Settings);
        Unloaded += (_, _) => Observe(null);
    }
    private static void SettingsChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var view = (BackgroundOptions)owner;
        view.Sync();
        if (view.IsLoaded) view.Observe((AppSettings?)args.NewValue);
    }
    private void Observe(AppSettings? settings)
    {
        if (_observed is not null) _observed.PropertyChanged -= PreferenceChanged;
        _observed = settings;
        if (settings is not null) settings.PropertyChanged += PreferenceChanged;
        Sync();
    }
    private void PreferenceChanged(object? sender, PropertyChangedEventArgs args)
    { if (!_updating && args.PropertyName is nameof(AppSettings.AmbientEnabled) or nameof(AppSettings.BackgroundColor)) Sync(); }
    private void Sync()
    {
        if (AmbientToggle is null || Settings is not { } settings) return;
        _updating = true;
        try
        {
            AmbientToggle.IsChecked = settings.AmbientEnabled;
            ColorPanel.Opacity = settings.AmbientEnabled ? .65 : 1;
            ColorHex.Text = settings.BackgroundColor;
            UiThemeService.TryBackgroundColor(settings.BackgroundColor, out var color);
            ColorSwatch.Background = new SolidColorBrush(color);
            ColorError.Visibility = Visibility.Collapsed;
        }
        finally { _updating = false; }
    }
    private void Ambient_Changed(object sender, RoutedEventArgs args)
    {
        if (_updating || Settings is not { } settings) return;
        settings.AmbientEnabled = AmbientToggle.IsChecked == true;
        ColorPanel.Opacity = settings.AmbientEnabled ? .65 : 1;
        UiThemeService.ConfigureBackground(settings);
    }
    private void ColorHex_Changed(object sender, TextChangedEventArgs args)
    {
        if (_updating || Settings is not { } settings) return;
        var valid = UiThemeService.TryBackgroundColor(ColorHex.Text, out var color);
        ColorError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        if (!valid) return;
        _updating = true;
        try { settings.BackgroundColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}"; ColorSwatch.Background = new SolidColorBrush(color); }
        finally { _updating = false; }
        UiThemeService.ConfigureBackground(settings);
    }
    private void ColorHex_LostFocus(object sender, RoutedEventArgs args) { if (ColorError.Visibility != Visibility.Visible) Sync(); }
    private void PickColor_Click(object sender, RoutedEventArgs args)
    {
        if (Settings is not { } settings) return;
        UiThemeService.TryBackgroundColor(settings.BackgroundColor, out var initial);
        var customColors = Marshal.AllocHGlobal(16 * sizeof(int));
        try
        {
            Marshal.Copy(new int[16], 0, customColors, 16);
            var dialog = new ChooseColorData { Size = Marshal.SizeOf<ChooseColorData>(), Owner = Window.GetWindow(this) is { } owner ? new WindowInteropHelper(owner).Handle : IntPtr.Zero,
                Result = initial.R | initial.G << 8 | initial.B << 16, CustomColors = customColors, Flags = 3 };
            if (ChooseColor(ref dialog)) ColorHex.Text = $"#{dialog.Result & 255:X2}{dialog.Result >> 8 & 255:X2}{dialog.Result >> 16 & 255:X2}";
        }
        finally { Marshal.FreeHGlobal(customColors); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorData
    {
        public int Size;
        public IntPtr Owner, Instance;
        public int Result;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData, Hook, Template;
    }
    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref ChooseColorData dialog);
}

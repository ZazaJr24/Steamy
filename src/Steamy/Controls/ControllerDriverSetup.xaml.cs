using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Controls;

public partial class ControllerDriverSetup : UserControl
{
    private bool _checked;
    public ControllerDriverSetup()
    {
        InitializeComponent();
        Loaded += (_, _) => CheckWhenVisible();
        IsVisibleChanged += (_, _) => CheckWhenVisible();
    }
    private void CheckWhenVisible()
    {
        if (_checked || !IsLoaded || !IsVisible) return;
        if (App.Services?.GetService<ControllersViewModel>() is not { } model) return;
        DataContext = model;
        if (!model.CheckDriversCommand.CanExecute(null)) return;
        _checked = true;
        model.CheckDriversCommand.Execute(null);
    }
}

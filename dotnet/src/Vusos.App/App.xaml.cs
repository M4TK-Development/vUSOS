using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace Vusos.App;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "vUSOS" };
}

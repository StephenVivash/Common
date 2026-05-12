using Microsoft.Extensions.DependencyInjection;

namespace WattCycleApp
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            //Window window = base.CreateWindow(activationState);
            Window window = new Window(new AppShell());
            window.Destroying += (s, e) => {/* AppPreferences.Save(); AiPreferences.Save(); */};
            window.Created += (s, e) =>
            {
                window.X = 300;
                window.Y = 20;
                window.Width = 380;
                window.Height = 500;
            };
            return window;
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using RVNEX.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.ViewManagement;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace RVNEX
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private readonly UISettings _systemSettings;
        private readonly AccessibilitySettings _accessibilitySettings;
        private readonly bool _startupIsDark;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            var settings = new AppSettingsService();
            settings.Load();
            _systemSettings = new UISettings();
            _accessibilitySettings = new AccessibilitySettings();
            _startupIsDark = ThemePalette.ResolveIsDark(settings.Theme, _systemSettings);
            RequestedTheme = _startupIsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (_window is not null)
            {
                _window.Activate();
                return;
            }

            ThemePalette.Apply(Resources, _startupIsDark, _systemSettings, _accessibilitySettings.HighContrast);
            _window = new MainWindow();
            _window.Closed += (_, _) => _window = null;
            _window.Activate();
        }
    }
}

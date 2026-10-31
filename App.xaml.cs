using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace scarpa_connection_manager_win
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        // 1. Add this public static property so the rest of the app can find the window
        public static Window MainWindow { get; private set; } = null!;

        // --- START SECURITY FIX: GLOBAL MASTER PASSWORD ---
        // Store as SecureString to prevent memory scraping
        public static System.Security.SecureString? ActiveMasterPassword { get; set; }
        // --- END SECURITY FIX ---

        private Window m_window = null!;

        public App()
        {
            this.InitializeComponent();
            UnhandledException += (s, e) => System.IO.File.WriteAllText("crash.txt", e.Exception.ToString());
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();

            // 2. Assign the static property right here
            MainWindow = m_window;

            m_window.Activate();
        }
    }
}
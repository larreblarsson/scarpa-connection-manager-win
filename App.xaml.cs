using System;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace scarpa_connection_manager_win
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private static readonly object s_logLock = new();
        private static SecureString? s_activeMasterPassword;
        private Window? m_window;

        public static Window MainWindow { get; private set; } = null!;

        /// <summary>
        /// Holds the active master password in a read-only SecureString and disposes
        /// any previous instance automatically when replaced or cleared.
        /// </summary>
        public static SecureString? ActiveMasterPassword
        {
            get => s_activeMasterPassword;
            set
            {
                if (ReferenceEquals(s_activeMasterPassword, value))
                {
                    return;
                }

                s_activeMasterPassword?.Dispose();

                if (value is not null && !value.IsReadOnly())
                {
                    value.MakeReadOnly();
                }

                s_activeMasterPassword = value;
            }
        }

        /// <summary>
        /// Immediately disposes and clears the active master password (e.g., on vault lock or exit).
        /// </summary>
        public static void ClearMasterPassword()
        {
            ActiveMasterPassword = null;
        }

        public App()
        {
            InitializeComponent();
            RegisterGlobalExceptionHandlers();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Closed += OnMainWindowClosed;

            MainWindow = m_window;
            m_window.Activate();
        }

        private void OnMainWindowClosed(object sender, WindowEventArgs args)
        {
            ClearMasterPassword();

            if (m_window is not null)
            {
                m_window.Closed -= OnMainWindowClosed;
                m_window = null;
            }

            MainWindow = null!;
        }

        private void RegisterGlobalExceptionHandlers()
        {
            // 1. UI thread exceptions (WinUI DispatcherQueue)
            UnhandledException += (_, e) =>
            {
                LogCrash("WinUI.UnhandledException", e.Exception);
            };

            // 2. Background / worker thread exceptions
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    LogCrash($"AppDomain.UnhandledException (IsTerminating={e.IsTerminating})", ex);
                }
            };

            // 3. Faulted background Tasks (e.g., async SSH/socket operations)
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }

        private static void LogCrash(string source, Exception? ex)
        {
            if (ex is null)
            {
                return;
            }

            try
            {
                lock (s_logLock)
                {
                    string logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ScarpaConnectionManager");
                    Directory.CreateDirectory(logDir);

                    string logPath = Path.Combine(logDir, "crash.txt");

                    // Rotate log if it exceeds 1 MB
                    FileInfo info = new(logPath);
                    if (info.Exists && info.Length > 1_048_576)
                    {
                        string backupPath = Path.Combine(logDir, "crash.old.txt");
                        File.Move(logPath, backupPath, overwrite: true);
                    }

                    string entry =
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]{Environment.NewLine}" +
                        $"{ex}{Environment.NewLine}" +
                        $"{new string('-', 60)}{Environment.NewLine}";

                    File.AppendAllText(logPath, entry);
                }
            }
            catch
            {
                // Never allow logging failures to escape and trigger recursive crashes
            }
        }
    }
}
using System;
using System.IO;
using System.Windows;

namespace ZStationStudio
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                LogException("AppDomain", args.ExceptionObject as Exception);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                LogException("Dispatcher", args.Exception);
                args.Handled = true;
                MessageBox.Show($"Lỗi ứng dụng: {args.Exception.Message}", "Z-Station Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogException("UnobservedTask", args.Exception);
                args.SetObserved();
            };

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            string logPath = Path.Combine(Path.GetTempPath(), "debug_close.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] App.OnExit: code={e.ApplicationExitCode}\n\n");
            base.OnExit(e);
        }

        private static void LogException(string source, Exception? ex)
        {
            if (ex == null) return;
            string logPath = Path.Combine(Path.GetTempPath(), "ZStationStudio_crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now}] [{source}] {ex}\n\n");
        }
    }
}

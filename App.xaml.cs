using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace SPWare.VirtualSoundCanvas
{
    public partial class App : Application
    {
        /// <summary>
        /// 크래시 로그 경로. 앱이 그냥 사라져버리면 원인을 알 수가 없어서,
        /// 처리되지 않은 예외를 파일로 남깁니다.
        /// </summary>
        private static string CrashLogPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SPWare", "WindSound", "crash.log");

        // ---- 중복 실행 방지 ----
        // 같은 프로그램을 두 번 띄우면 가상 MIDI 포트와 오디오 장치, 설정 파일을 서로 뺏고 소리가 겹친다.
        // 이름 있는 뮤텍스로 이미 실행 중인지 확인해서, 두 번째 실행은 첫 번째 창을 앞으로 가져오고 바로 끝낸다.
        private static Mutex? _singleInstanceMutex; // 프로세스가 끝날 때까지 들고 있어야 한다(GC로 풀리면 안 됨)

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        private static void ActivateRunningInstance()
        {
            try
            {
                using var me = Process.GetCurrentProcess();
                foreach (var p in Process.GetProcessesByName(me.ProcessName))
                {
                    using (p)
                    {
                        if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                        if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9 /* SW_RESTORE */);
                        SetForegroundWindow(p.MainWindowHandle);
                        return;
                    }
                }
            }
            catch { /* 앞으로 가져오기에 실패해도 중복 실행 차단은 유지 */ }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(true, @"Local\SPWare.WindSound.Public.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                ActivateRunningInstance();
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            // UI 스레드에서 터진 예외
            DispatcherUnhandledException += (_, args) =>
            {
                WriteCrash("UI 스레드", args.Exception);
                MessageBox.Show(
                    SPWare.VirtualSoundCanvas.Localization.Loc.T($"오류가 발생했습니다.\n\n{args.Exception.Message}\n\n자세한 내용:\n{CrashLogPath}"),
                    "SPWare Wind Sound", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true; // 앱이 즉시 죽지 않게
            };

            // 워커/오디오 스레드에서 터진 예외 (여기 오면 앱은 종료됩니다)
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                WriteCrash("백그라운드 스레드", args.ExceptionObject as Exception);

            // await 하지 않은 Task에서 터진 예외
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                WriteCrash("Task", args.Exception);
                args.SetObserved();
            };
        }

        private static void WriteCrash(string source, Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
                File.AppendAllText(CrashLogPath,
                    $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] =====\n{ex}\n\n");
            }
            catch
            {
                // 크래시 로그를 못 쓰는 상황이라면 더 할 수 있는 게 없습니다.
            }
        }
    }
}

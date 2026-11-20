using System;
using System.Threading;
using System.Windows.Forms;
using Koxmix.Forms;

namespace Koxmix
{
    internal static class Program
    {
        private static Mutex _appMutex;

        [STAThread]
        static void Main()
        {
            const string mutexName = "Global\\Koxmix_SingleInstance_Mutex_2026";
            _appMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                MessageBox.Show("Koxmix is already running!", "Failed to start application", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm());
            }
            finally
            {
                _appMutex.ReleaseMutex();
                _appMutex.Dispose();
            }
        }
    }
}
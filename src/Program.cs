using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AlapCamera
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static Mutex _single;

        [STAThread]
        static void Main()
        {
            bool created;
            _single = new Mutex(true, "AlapCamera_SingleInstance_Mutex", out created);
            if (!created) return;

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                Theme.Scale = Math.Max(1f, g.DpiX / 96f);

            Application.Run(new MainForm());
            GC.KeepAlive(_single);
        }
    }
}

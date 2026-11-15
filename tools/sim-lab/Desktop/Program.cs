using System;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using SimLab;

namespace SimLab.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();

            WebApplication app = null;
            try
            {
                // The lab's engine runs inside this program, on a free port of this computer only.
                app = LabServer.Create("http://127.0.0.1:0", true);
                app.Start();
                Application.Run(new MainForm(LabServer.AddressOf(app)));
            }
            catch (Exception e)
            {
                MessageBox.Show("The lab could not start:\n\n" + e.Message, "Armor impact lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (app != null) app.StopAsync().GetAwaiter().GetResult();
            }
        }
    }
}

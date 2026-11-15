using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SimLab.Desktop
{
    // The window: the lab's page, shown by WebView2.
    internal sealed class MainForm : Form
    {
        private readonly string address;
        private readonly WebView2 view = new WebView2();

        public MainForm(string address)
        {
            this.address = address;
            Text = "Armor impact lab";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1440, 900);
            MinimumSize = new Size(900, 600);
            view.Dock = DockStyle.Fill;
            Controls.Add(view);
            Load += async (sender, args) => await Start();
        }

        private async System.Threading.Tasks.Task Start()
        {
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArmorImpactLab", "WebView2");
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, data);
                await view.EnsureCoreWebView2Async(environment);
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
                view.CoreWebView2.Navigate(address);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                DialogResult answer = MessageBox.Show(
                    "This program shows its pages with WebView2, which is part of Microsoft Edge and normally already installed.\n\n" +
                    "It was not found. Open the download page?", "Armor impact lab", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true });
                Close();
            }
            catch (Exception e)
            {
                MessageBox.Show("The page could not be shown:\n\n" + e.Message, "Armor impact lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }
    }
}

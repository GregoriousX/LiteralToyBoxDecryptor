// SPDX-License-Identifier: GPL-3.0-only
namespace ToyBoxDecryptor.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
#pragma warning disable WFO5001 // .NET's native dark control support supplements our own theme.
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new MainForm();
        if (args.Length >= 2 && args[0] == "--render-preview") {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-30000, -30000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        if (args.Length >= 3 && args[0] == "--ui-smoke") {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
            form.Shown += async (_, _) => {
                try { await form.SmokeAsync(args[1], args[2], args.Length > 3 && args[3] == "3" ? Core.GameProfile.DI3 : Core.GameProfile.Auto); }
                catch (Exception e) { File.WriteAllText(Path.Combine(args[2], "ui-failure.txt"), e.ToString()); Environment.ExitCode = 1; }
                finally { form.Close(); }
            };
        }
        else if (args.Length > 0) form.Shown += async (_, _) => await form.AddPathsAsync(args);
        Application.Run(form);
    }
}

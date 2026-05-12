using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MqttAgent.Utils;

public static class SessionHelper
{
    public static void Run(string[] args)
    {
        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "session_helper.log");
        void Log(string msg) { try { File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"); } catch {} }

        try
        {
            Log($"Helper started with args: {string.Join(" ", args)}");

            // Enable DPI awareness for accurate multi-monitor detection
            NativeMethods.SetProcessDPIAware();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            if (args.Contains("--messagebox"))
            {
                var titleIdx = Array.IndexOf(args, "--title");
                var msgIdx = Array.IndexOf(args, "--message");
                var typeIdx = Array.IndexOf(args, "--type");
                var iconIdx = Array.IndexOf(args, "--icon");
                var timeoutIdx = Array.IndexOf(args, "--timeout");

                var title = (titleIdx >= 0 && titleIdx + 1 < args.Length) ? args[titleIdx + 1] : "Notification";
                var message = (msgIdx >= 0 && msgIdx + 1 < args.Length) ? args[msgIdx + 1] : "";
                var typeStr = (typeIdx >= 0 && typeIdx + 1 < args.Length) ? args[typeIdx + 1] : "MB_OK";
                var iconStr = (iconIdx >= 0 && iconIdx + 1 < args.Length) ? args[iconIdx + 1] : "MB_ICONINFORMATION";
                int timeoutMs = 0;
                if (timeoutIdx >= 0 && timeoutIdx + 1 < args.Length) int.TryParse(args[timeoutIdx + 1], out timeoutMs);

                MessageBoxButtons buttons = MessageBoxButtons.OK;
                if (typeStr.Contains("OKCANCEL", StringComparison.OrdinalIgnoreCase)) buttons = MessageBoxButtons.OKCancel;
                else if (typeStr.Contains("ABORTRETRYIGNORE", StringComparison.OrdinalIgnoreCase)) buttons = MessageBoxButtons.AbortRetryIgnore;
                else if (typeStr.Contains("YESNOCANCEL", StringComparison.OrdinalIgnoreCase)) buttons = MessageBoxButtons.YesNoCancel;
                else if (typeStr.Contains("YESNO", StringComparison.OrdinalIgnoreCase)) buttons = MessageBoxButtons.YesNo;
                else if (typeStr.Contains("RETRYCANCEL", StringComparison.OrdinalIgnoreCase)) buttons = MessageBoxButtons.RetryCancel;

                MessageBoxIcon mIcon = MessageBoxIcon.Information;
                if (iconStr.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || iconStr.Contains("HAND", StringComparison.OrdinalIgnoreCase) || iconStr.Contains("STOP", StringComparison.OrdinalIgnoreCase)) mIcon = MessageBoxIcon.Error;
                else if (iconStr.Contains("QUESTION", StringComparison.OrdinalIgnoreCase)) mIcon = MessageBoxIcon.Question;
                else if (iconStr.Contains("WARNING", StringComparison.OrdinalIgnoreCase) || iconStr.Contains("EXCLAMATION", StringComparison.OrdinalIgnoreCase)) mIcon = MessageBoxIcon.Warning;

                Log($"Showing MessageBox: {title} - {message} (Buttons: {buttons}, Icon: {mIcon}, Timeout: {timeoutMs})");

                if (timeoutMs > 0)
                {
                    var timer = new System.Windows.Forms.Timer { Interval = timeoutMs };
                    timer.Tick += (s, e) => { timer.Stop(); SendKeys.SendWait("{ESC}"); };
                    timer.Start();
                }

                MessageBox.Show(message, title, buttons, mIcon);
                return;
            }

            if (args.Contains("--banner"))
            {
                var msgIdx = Array.IndexOf(args, "--message");
                var message = (msgIdx >= 0 && msgIdx + 1 < args.Length) ? args[msgIdx + 1] : "";
                Log($"Showing Banner: {message}");
                ShowBanner(message);
                return;
            }

            if (args.Contains("--screenshot-helper"))
            {
                HandleScreenshot(args, Log);
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }
    }

    private static void ShowBanner(string message)
    {
        using var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            WindowState = FormWindowState.Maximized,
            BackColor = Color.Black,
            TopMost = true,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen
        };

        var label = new Label
        {
            Text = message,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 48, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };

        form.Controls.Add(label);
        form.Click += (s, e) => form.Close();
        label.Click += (s, e) => form.Close();

        // Close after 10 seconds
        var timer = new System.Windows.Forms.Timer { Interval = 10000 };
        timer.Tick += (s, e) => form.Close();
        timer.Start();

        Application.Run(form);
    }

    private static void HandleScreenshot(string[] args, Action<string> Log)
    {
        if (args.Contains("--list-screens"))
        {
            var screenList = new List<object>();
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.Rect lprcMonitor, IntPtr dwData) =>
            {
                var mi = NativeMethods.MONITORINFOEX.Create();
                if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    screenList.Add(new
                    {
                        index = screenList.Count,
                        name = mi.szDevice,
                        isPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                        bounds = new { x = mi.rcMonitor.Left, y = mi.rcMonitor.Top, width = mi.rcMonitor.Right - mi.rcMonitor.Left, height = mi.rcMonitor.Bottom - mi.rcMonitor.Top },
                        workArea = new { x = mi.rcWork.Left, y = mi.rcWork.Top, width = mi.rcWork.Right - mi.rcWork.Left, height = mi.rcWork.Bottom - mi.rcWork.Top }
                    });
                }
                return true;
            }, IntPtr.Zero);

            var json = System.Text.Json.JsonSerializer.Serialize(screenList);
            string? listOutPath = null;
            var listOutIdx = Array.IndexOf(args, "--out");
            if (listOutIdx >= 0 && listOutIdx + 1 < args.Length)
                listOutPath = args[listOutIdx + 1];

            if (!string.IsNullOrEmpty(listOutPath)) File.WriteAllText(listOutPath, json);
            else Console.WriteLine(json);
            return;
        }

        int quality = 75;
        var qualityIdx = Array.IndexOf(args, "--quality");
        if (qualityIdx >= 0 && qualityIdx + 1 < args.Length) int.TryParse(args[qualityIdx + 1], out quality);

        string? outPath = null;
        var outIdx = Array.IndexOf(args, "--out");
        if (outIdx >= 0 && outIdx + 1 < args.Length) outPath = args[outIdx + 1];

        string display = "all";
        var displayIdx = Array.IndexOf(args, "--display");
        if (displayIdx >= 0 && displayIdx + 1 < args.Length) display = args[displayIdx + 1];

        bool usePng = args.Contains("--png");

        var screensList = new List<Rectangle>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.Rect lprcMonitor, IntPtr dwData) =>
        {
            screensList.Add(new Rectangle(lprcMonitor.Left, lprcMonitor.Top, lprcMonitor.Right - lprcMonitor.Left, lprcMonitor.Bottom - lprcMonitor.Top));
            return true;
        }, IntPtr.Zero);

        if (screensList.Count == 0 && Screen.PrimaryScreen != null)
            screensList.Add(Screen.PrimaryScreen.Bounds);

        Rectangle[] targetBounds;
        if (display.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            targetBounds = screensList.ToArray();
        }
        else
        {
            var byName = new List<Rectangle>();
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.Rect lprcMonitor, IntPtr dwData) =>
            {
                var mi = NativeMethods.MONITORINFOEX.Create();
                if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    if (string.Equals(mi.szDevice, display, StringComparison.OrdinalIgnoreCase) || display.Contains(mi.szDevice.Replace("\\\\.\\", ""), StringComparison.OrdinalIgnoreCase))
                        byName.Add(new Rectangle(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top));
                }
                return true;
            }, IntPtr.Zero);

            if (byName.Count > 0) targetBounds = byName.ToArray();
            else if (int.TryParse(display, out int idx) && idx >= 0 && idx < screensList.Count) targetBounds = new[] { screensList[idx] };
            else targetBounds = new[] { screensList[0] };
        }

        if (targetBounds.Length == 0) return;

        int minX = targetBounds.Min(s => s.X);
        int minY = targetBounds.Min(s => s.Y);
        int maxX = targetBounds.Max(s => s.Right);
        int maxY = targetBounds.Max(s => s.Bottom);
        int width = maxX - minX;
        int height = maxY - minY;

        using var bitmap = new Bitmap(width, height, usePng ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            if (usePng) g.Clear(Color.FromArgb(0, 0, 0, 0));
            foreach (var bounds in targetBounds)
                g.CopyFromScreen(bounds.X, bounds.Y, bounds.X - minX, bounds.Y - minY, bounds.Size);
        }

        if (!string.IsNullOrEmpty(outPath))
        {
            if (usePng) bitmap.Save(outPath, ImageFormat.Png);
            else
            {
                var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
                if (codec != null)
                {
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    bitmap.Save(outPath, codec, encoderParams);
                }
                else bitmap.Save(outPath, ImageFormat.Jpeg);
            }
        }
        else
        {
            using var ms = new MemoryStream();
            if (usePng) bitmap.Save(ms, ImageFormat.Png);
            else bitmap.Save(ms, ImageFormat.Jpeg);
            Console.Write("data:image/" + (usePng ? "png" : "jpeg") + ";base64," + Convert.ToBase64String(ms.ToArray()));
        }
    }
}

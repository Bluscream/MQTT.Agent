using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Modern_Windows_Message_Box_Generator.CLI;

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


            var tasks = new List<Task>();

            if (args.Contains("--messagebox") || args.Contains("--toast") || args.Contains("--xsoverlay") || args.Contains("--ovrtoolkit") || args.Contains("--banner"))
            {
                Log("Invoking Unified Notification logic...");
                try { Modern_Windows_Message_Box_Generator.CLI.Program.Main(args).Wait(); }
                catch (Exception ex) { Log($"Notification Error: {ex.Message}"); }
                Log("Unified notification logic completed.");
                return;
            }

            if (args.Contains("--screenshot-helper"))
            {
                HandleScreenshot(args, Log);
                return;
            }

            if (args.Contains("--stream-helper"))
            {
                HandleStream(args, Log);
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }
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
        bool forceGdi = args.Contains("--force-gdi");

        // Try DXGI first unless forced to GDI or capturing all screens (DXGI is per-monitor)
        if (!forceGdi && !usePng && !display.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var dxgi = new MqttAgent.Utils.Capture.DxgiCaptureBackend();
                dxgi.Initialize(display);
                var frameTask = dxgi.CaptureFrame(quality);
                frameTask.Wait();
                var bytes = frameTask.Result;
                
                if (bytes != null && bytes.Length > 100)
                {
                    Log($"DXGI capture successful ({bytes.Length} bytes)");
                    if (!string.IsNullOrEmpty(outPath)) File.WriteAllBytes(outPath, bytes);
                    else Console.Write("data:image/jpeg;base64," + Convert.ToBase64String(bytes));
                    return;
                }
                Log("DXGI capture returned no data, falling back to GDI.");
            }
            catch (Exception ex)
            {
                Log($"DXGI capture failed: {ex.Message}. Falling back to GDI.");
            }
        }

        // Fallback to GDI (Existing logic)
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

    private static void HandleStream(string[] args, Action<string> Log)
    {
        int quality = 50;
        int fps = 10;
        string display = "all";
        int port = 0;

        var qualityIdx = Array.IndexOf(args, "--quality");
        if (qualityIdx >= 0 && qualityIdx + 1 < args.Length) int.TryParse(args[qualityIdx + 1], out quality);

        var fpsIdx = Array.IndexOf(args, "--fps");
        if (fpsIdx >= 0 && fpsIdx + 1 < args.Length) int.TryParse(args[fpsIdx + 1], out fps);

        var displayIdx = Array.IndexOf(args, "--display");
        if (displayIdx >= 0 && displayIdx + 1 < args.Length) display = args[displayIdx + 1];
        
        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length) int.TryParse(args[portIdx + 1], out port);

        Log($"Stream helper starting (Display: {display}, FPS: {fps}, Quality: {quality}, Port: {port})");

        MqttAgent.Utils.Capture.ICaptureBackend backend;
        if (display.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            backend = new MqttAgent.Utils.Capture.GdiCaptureBackend();
        }
        else
        {
            backend = new MqttAgent.Utils.Capture.DxgiCaptureBackend();
        }

        try
        {
            backend.Initialize(display);
        }
        catch (Exception ex)
        {
            Log($"Backend initialization failed: {ex.Message}");
            return;
        }

        using (backend)
        {
            var sw = new Stopwatch();
            Stream outStream;
            System.Net.Sockets.TcpClient? client = null;
            
            if (port > 0)
            {
                try
                {
                    client = new System.Net.Sockets.TcpClient("127.0.0.1", port);
                    outStream = client.GetStream();
                }
                catch (Exception ex)
                {
                    Log($"Failed to connect to port {port}: {ex.Message}");
                    return;
                }
            }
            else
            {
                outStream = Console.OpenStandardOutput();
            }

            var boundary = System.Text.Encoding.ASCII.GetBytes("--frame\r\n");
            var newLine = System.Text.Encoding.ASCII.GetBytes("\r\n");

            try
            {
                while (true)
                {
                    sw.Restart();
                    var frameTask = backend.CaptureFrame(quality);
                    frameTask.Wait();
                    var bytes = frameTask.Result;

                    if (bytes != null)
                    {
                        var header = System.Text.Encoding.ASCII.GetBytes($"Content-Type: image/jpeg\r\nContent-Length: {bytes.Length}\r\n\r\n");
                        outStream.Write(boundary, 0, boundary.Length);
                        outStream.Write(header, 0, header.Length);
                        outStream.Write(bytes, 0, bytes.Length);
                        outStream.Write(newLine, 0, newLine.Length);
                        outStream.Flush();
                    }
                    else if (!display.Equals("all", StringComparison.OrdinalIgnoreCase) && backend is MqttAgent.Utils.Capture.DxgiCaptureBackend)
                    {
                        // DXGI might have lost access (lock screen), try to re-initialize
                        try { backend.Initialize(display); } catch { }
                    }

                    sw.Stop();
                    int targetDelay = 1000 / Math.Max(1, Math.Min(60, fps));
                    int remainingDelay = targetDelay - (int)sw.ElapsedMilliseconds;
                    if (remainingDelay > 0) Thread.Sleep(remainingDelay);
                }
            }
            catch (Exception ex)
            {
                Log($"Stream helper error: {ex.Message}");
            }
            finally
            {
                client?.Dispose();
            }
        }
    }
}

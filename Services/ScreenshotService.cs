using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using MqttAgent.Utils;
using System.Xml.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text.Json;

namespace MqttAgent.Services
{
    public class ScreenshotService
    {
        private readonly ProcessService _processService;
        private readonly MultiMonitorToolService _monitorService;

        public ScreenshotService(ProcessService processService, MultiMonitorToolService monitorService)
        {
            _processService = processService;
            _monitorService = monitorService;
        }

        public async Task<string> CaptureScreenshot(string desktop = "Default", int quality = 75, string display = "all", string format = "png")
        {
            List<string> errors = new List<string>();
            bool usePng = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);
            string mimeType = usePng ? "image/png" : "image/jpeg";
            string dataUriPrefix = $"data:{mimeType};base64,";

            // Resolve friendly name to device name or index if needed
            string resolvedDisplay = await _monitorService.ResolveMonitorName(display);
            if (!string.Equals(resolvedDisplay, display))
            {
                Console.WriteLine($"[ScreenshotService] Resolved friendly name '{display}' to '{resolvedDisplay}'");
            }

            // Helper to try a specific desktop with multiple methods
            async Task<string?> TryCaptureDesktop(string targetDesktop)
            {
                string desktopStr = targetDesktop.Contains("\\") ? targetDesktop : $"winsta0\\{targetDesktop}";
                var sessionId = _processService.GetActiveConsoleSessionId();
                var helperPath = Process.GetCurrentProcess().MainModule?.FileName;
 
                // Use application-relative path for extraction to ensure Session 1 user can access it
                string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

                if (!string.IsNullOrEmpty(helperPath))
                {
                    string args = $"--screenshot-helper --quality {quality} --display {resolvedDisplay}";
                    if (usePng) args += " --png";

                    // Method 1: Helper as Active User (Best for 'Default' desktop to bypass DRM/UAC)
                    if (sessionId > 0 && targetDesktop.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var extension = usePng ? "png" : "jpg";
                            var tempFile = Path.Combine(tempDir, $"screenshot_usr_{Guid.NewGuid()}.{extension}");
                            string usrArgs = $"{args} --out \"{tempFile}\"";
                            Console.WriteLine($"[ScreenshotService] Trying helper as User {sessionId} on {desktopStr} (Display: {resolvedDisplay}, Format: {format})");
                            await _processService.StartProcess(helperPath, usrArgs, waitForExit: true, asUser: sessionId.ToString(), desktop: desktopStr);
                            
                            if (File.Exists(tempFile))
                            {
                                var bytes = await File.ReadAllBytesAsync(tempFile);
                                try { File.Delete(tempFile); } catch { }
                                if (bytes.Length > 100) 
                                {
                                    Console.WriteLine($"[ScreenshotService] SUCCESS (UserHelper): {bytes.Length} bytes");
                                    return dataUriPrefix + Convert.ToBase64String(bytes);
                                }
                                else errors.Add($"User Helper on {desktopStr} returned invalid file ({bytes.Length} bytes).");
                            }
                            else errors.Add($"User Helper on {desktopStr} failed to create file.");
                        }
                        catch (Exception ex) { errors.Add($"User Helper Exception: {ex.Message}"); }
                    }

                    // Method 2: Helper as SYSTEM (Best for 'Winlogon' or when no user is logged in)
                    try
                    {
                        var extension = usePng ? "png" : "jpg";
                        var tempFile = Path.Combine(tempDir, $"screenshot_sys_{Guid.NewGuid()}.{extension}");
                        string sysArgs = $"{args} --out \"{tempFile}\"";
                        Console.WriteLine($"[ScreenshotService] Trying helper as SYSTEM on {desktopStr} (Display: {resolvedDisplay}, Format: {format})");
                        await _processService.StartProcess(helperPath, sysArgs, waitForExit: true, asUser: null, desktop: desktopStr);
                        
                        if (File.Exists(tempFile))
                        {
                            var bytes = await File.ReadAllBytesAsync(tempFile);
                            try { File.Delete(tempFile); } catch { }
                            if (bytes.Length > 100) 
                            {
                                Console.WriteLine($"[ScreenshotService] SUCCESS (SysHelper): {bytes.Length} bytes");
                                return dataUriPrefix + Convert.ToBase64String(bytes);
                            }
                            else errors.Add($"SYSTEM Helper on {desktopStr} returned invalid/empty file ({bytes.Length} bytes).");
                        }
                        else errors.Add($"SYSTEM Helper on {desktopStr} failed to create file.");
                    }
                    catch (Exception ex) { errors.Add($"SYSTEM Helper Exception: {ex.Message}"); }
                }
                else
                {
                    errors.Add("Helper executable path not found.");
                }

                // Method 3: Direct Capture from Service Process
                try
                {
                    Console.WriteLine($"[ScreenshotService] Trying Direct Capture on {desktopStr} (Display: {resolvedDisplay})");
                    var allScreens = System.Windows.Forms.Screen.AllScreens;
                    System.Windows.Forms.Screen[] targets;

                    if (resolvedDisplay.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        targets = allScreens;
                    }
                    else if (int.TryParse(resolvedDisplay, out int idx) && idx >= 0 && idx < allScreens.Length)
                    {
                        targets = [allScreens[idx]];
                    }
                    else
                    {
                        var byName = allScreens.Where(s => 
                            string.Equals(s.DeviceName, resolvedDisplay, StringComparison.OrdinalIgnoreCase) ||
                            resolvedDisplay.Contains(s.DeviceName.Replace("\\\\.\\", ""), StringComparison.OrdinalIgnoreCase)
                        ).ToArray();

                        if (byName.Length > 0)
                            targets = byName;
                        else
                            targets = [System.Windows.Forms.Screen.PrimaryScreen ?? allScreens[0]];
                    }

                    if (targets.Length > 0)
                    {
                        int minX = targets.Min(s => s.Bounds.X);
                        int minY = targets.Min(s => s.Bounds.Y);
                        int maxX = targets.Max(s => s.Bounds.Right);
                        int maxY = targets.Max(s => s.Bounds.Bottom);
                        int width = maxX - minX;
                        int height = maxY - minY;

                        using (Bitmap bitmap = new Bitmap(width, height, usePng ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb))
                        {
                            using (Graphics g = Graphics.FromImage(bitmap))
                            {
                                if (usePng) g.Clear(Color.FromArgb(0, 0, 0, 0));
                                foreach (var screen in targets)
                                {
                                    g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.X - minX, screen.Bounds.Y - minY, screen.Bounds.Size);
                                }
                            }

                            using (MemoryStream ms = new MemoryStream())
                            {
                                if (usePng)
                                {
                                    bitmap.Save(ms, ImageFormat.Png);
                                }
                                else
                                {
                                    ImageCodecInfo? codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
                                    if (codec != null)
                                    {
                                        EncoderParameters encoderParams = new EncoderParameters(1);
                                        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                                        bitmap.Save(ms, codec, encoderParams);
                                    }
                                    else
                                    {
                                        bitmap.Save(ms, ImageFormat.Jpeg);
                                    }
                                }

                                byte[] bytes = ms.ToArray();
                                if (bytes.Length > 100)
                                {
                                    Console.WriteLine($"[ScreenshotService] SUCCESS (Direct): {bytes.Length} bytes");
                                    return dataUriPrefix + Convert.ToBase64String(bytes);
                                }
                                else
                                    errors.Add($"Direct capture on {desktopStr} resulted in empty image.");
                            }
                        }
                    }
                    else
                    {
                        errors.Add($"Direct capture on {desktopStr} failed: No target screens found.");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Direct capture exception on {desktopStr}: {ex.Message}");
                }

                return null;
            }

            // Attempt requested desktop first
            string? result = await TryCaptureDesktop(desktop);
            if (result != null) return result;

            // If requested was Default and we are locked/logged out, fallback to Winlogon automatically!
            if (desktop.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[ScreenshotService] Default desktop capture failed. Falling back to Winlogon desktop...");
                errors.Add("--- Falling back to Winlogon ---");
                result = await TryCaptureDesktop("Winlogon");
                if (result != null) return result;
            }

            return "ERROR: All screenshot methods failed.\n" + string.Join("\n", errors);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenWindowStation(string lpszWinSta, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll")]
        private static extern bool EnumDesktops(IntPtr hwinsta, EnumDesktopsDelegate lpEnumCallback, IntPtr lParam);

        private delegate bool EnumDesktopsDelegate(string lpszDesktop, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseWindowStation(IntPtr hWinSta);

        private const uint MAXIMUM_ALLOWED = 0x02000000;

        public async Task<List<object>> ListScreens()
        {
            try
            {
                // 1. Use MultiMonitorToolService as the primary source
                var json = await _monitorService.GetMonitorsAsync("json");
                if (!string.IsNullOrEmpty(json) && json.Trim() != "[]")
                {
                    var mmtMonitors = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json);
                    if (mmtMonitors != null && mmtMonitors.Count > 0)
                    {
                        var result = new List<object>();
                        foreach (var mmt in mmtMonitors)
                        {
                            // Map MMT fields to our expected format
                            // MMT: "Short Monitor ID", "Name", "Primary", "Resolution", "Left-Top"
                            // Resolution sample: "3440 X 1440"
                            // Left-Top sample: "0, 0"
                            
                            var res = mmt.GetValueOrDefault("resolution") ?? mmt.GetValueOrDefault("Resolution");
                            res.TryParseResolution(out int width, out int height);
 
                            var pos = mmt.GetValueOrDefault("left-top") ?? mmt.GetValueOrDefault("Left-Top");
                            pos.TryParsePosition(out int x, out int y);
 
                            result.Add(new
                            {
                                index = result.Count,
                                name = mmt.GetFriendlyMonitorName(),
                                deviceName = mmt.GetValueOrDefault("name") ?? "",
                                isPrimary = string.Equals(mmt.GetValueOrDefault("primary"), "Yes", StringComparison.OrdinalIgnoreCase) || string.Equals(mmt.GetValueOrDefault("Primary"), "Yes", StringComparison.OrdinalIgnoreCase),
                                bounds = new { x, y, width, height }
                            });
                        }
                        return result;
                    }
                }

                // 2. Fallback: Local EnumDisplayMonitors (likely Session 0 limited)
                Console.WriteLine("[ScreenshotService] ListScreens: MultiMonitorToolService returned nothing. Falling back to local Win32.");
                var screens = new List<object>();
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.Rect lprcMonitor, IntPtr dwData) =>
                {
                    var mi = NativeMethods.MONITORINFOEX.Create();
                    if (NativeMethods.GetMonitorInfo(hMonitor, ref mi))
                    {
                        screens.Add(new
                        {
                            index = screens.Count,
                            name = mi.szDevice,
                            deviceName = mi.szDevice,
                            isPrimary = (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                            bounds = new { x = mi.rcMonitor.Left, y = mi.rcMonitor.Top, width = mi.rcMonitor.Right - mi.rcMonitor.Left, height = mi.rcMonitor.Bottom - mi.rcMonitor.Top },
                            workArea = new { x = mi.rcWork.Left, y = mi.rcWork.Top, width = mi.rcWork.Right - mi.rcWork.Left, height = mi.rcWork.Bottom - mi.rcWork.Top }
                        });
                    }
                    return true;
                }, IntPtr.Zero);
                return screens;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenshotService] ListScreens Exception: {ex.Message}");
                return new List<object>();
            }
        }

        public List<string> ListDesktops()
        {
            List<string> desktops = new List<string>();
            try
            {
                IntPtr hWinSta = OpenWindowStation("WinSta0", false, MAXIMUM_ALLOWED);
                if (hWinSta != IntPtr.Zero)
                {
                    EnumDesktops(hWinSta, (name, param) =>
                    {
                        desktops.Add(name);
                        return true;
                    }, IntPtr.Zero);
                    CloseWindowStation(hWinSta);
                }
            }
            catch { }
            return desktops;
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MqttAgent.Utils.Capture
{
    public class GdiCaptureBackend : ICaptureBackend
    {
        public string Name => "GDI BitBlt";

        private string? _deviceName;
        private Screen? _targetScreen;

        public void Initialize(string deviceName)
        {
            _deviceName = deviceName;
            
            var allScreens = Screen.AllScreens;
            if (int.TryParse(deviceName, out int idx))
            {
                if (idx >= 0 && idx < allScreens.Length)
                {
                    _targetScreen = allScreens[idx];
                    return;
                }
            }

            _targetScreen = allScreens.FirstOrDefault(s => 
                string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) ||
                deviceName.Contains(s.DeviceName.Replace("\\\\.\\", ""), StringComparison.OrdinalIgnoreCase));
        }

        public async Task<byte[]?> CaptureFrame(int quality)
        {
            var screen = _targetScreen ?? Screen.PrimaryScreen;
            if (screen == null) return null;

            try
            {
                using (Bitmap bitmap = new Bitmap(screen.Bounds.Width, screen.Bounds.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.CopyFromScreen(screen.Bounds.X, screen.Bounds.Y, 0, 0, screen.Bounds.Size);
                    }

                    using (MemoryStream ms = new MemoryStream())
                    {
                        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");
                        if (codec != null)
                        {
                            var encoderParams = new EncoderParameters(1);
                            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                            bitmap.Save(ms, codec, encoderParams);
                        }
                        else
                        {
                            bitmap.Save(ms, ImageFormat.Jpeg);
                        }
                        return ms.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GdiCapture] Capture failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            // Nothing to dispose for GDI
        }
    }
}

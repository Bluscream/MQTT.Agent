using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MqttAgent.Utils.Capture
{
    public class DxgiCaptureBackend : ICaptureBackend
    {
        public string Name => "DXGI Desktop Duplication";

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _deskDupl;
        private ID3D11Texture2D? _stagingTexture;
        private string? _deviceName;
        private bool _isInitialized;

        public void Initialize(string deviceName)
        {
            if (_isInitialized && _deviceName == deviceName) return;
            
            Cleanup();
            _deviceName = deviceName;

            try
            {
                // Create Device only if needed
                if (_device == null)
                {
                    D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None, null!, out _device, out _context).CheckError();
                }
                
                using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                
                // Find correct output
                IDXGIOutput? targetOutput = null;
                bool isIndex = int.TryParse(deviceName, out int targetIndex);

                for (uint i = 0; ; i++)
                {
                    try
                    {
                        if (adapter.EnumOutputs(i, out var output).Failure) break;
                        
                        if (isIndex && i == targetIndex)
                        {
                            targetOutput = output;
                            break;
                        }

                        if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetOutput = output;
                            break;
                        }
                        output.Dispose();
                    }
                    catch { break; }
                }

                if (targetOutput == null) throw new Exception($"Output {deviceName} not found.");

                using var output1 = targetOutput.QueryInterface<IDXGIOutput1>();
                _deskDupl = output1.DuplicateOutput(_device);
                
                var desc = targetOutput.Description;
                int width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                int height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

                if (_stagingTexture == null || _stagingTexture.Description.Width != (uint)width || _stagingTexture.Description.Height != (uint)height)
                {
                    _stagingTexture?.Dispose();
                    var texDesc = new Texture2DDescription
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };
                    _stagingTexture = _device.CreateTexture2D(texDesc);
                }

                _isInitialized = true;
                targetOutput.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DxgiCapture] Initialization failed: {ex.Message}");
                Cleanup();
            }
        }

        public async Task<byte[]?> CaptureFrame(int quality)
        {
            if (!_isInitialized || _deskDupl == null || _context == null || _stagingTexture == null) return null;

            try
            {
                var result = _deskDupl.AcquireNextFrame(100, out var frameInfo, out var desktopResource);
                if (result.Failure) return null;

                using (desktopResource)
                using (var texture = desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    _context.CopyResource(_stagingTexture, texture);
                }

                _deskDupl.ReleaseFrame();

                var mapResource = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int width = (int)_stagingTexture.Description.Width;
                    int height = (int)_stagingTexture.Description.Height;

                    using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        var boundsRect = new System.Drawing.Rectangle(0, 0, width, height);
                        var mapDest = bitmap.LockBits(boundsRect, System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
                        
                        var sourcePtr = mapResource.DataPointer;
                        var destPtr = mapDest.Scan0;

                        for (int y = 0; y < height; y++)
                        {
                            DxgiNativeMethods.CopyMemory(destPtr, sourcePtr, width * 4);
                            sourcePtr = IntPtr.Add(sourcePtr, (int)mapResource.RowPitch);
                            destPtr = IntPtr.Add(destPtr, (int)mapDest.Stride);
                        }

                        bitmap.UnlockBits(mapDest);

                        using (var ms = new MemoryStream())
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
                finally
                {
                    _context.Unmap(_stagingTexture, 0);
                }
            }
            catch (Exception ex) when (ex.Message.Contains("AccessLost") || ex.HResult == (int)Vortice.DXGI.ResultCode.AccessLost)
            {
                _isInitialized = false;
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DxgiCapture] Capture failed: {ex.Message}");
                return null;
            }
        }

        private void Cleanup()
        {
            _isInitialized = false;
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _deskDupl?.Dispose();
            _deskDupl = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
        }

        public void Dispose()
        {
            Cleanup();
        }
    }

    internal static class DxgiNativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        public static extern void CopyMemory(IntPtr dest, IntPtr src, int count);
    }
}

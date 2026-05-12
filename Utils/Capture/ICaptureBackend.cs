using System;
using System.Threading.Tasks;

namespace MqttAgent.Utils.Capture
{
    public interface ICaptureBackend : IDisposable
    {
        string Name { get; }
        Task<byte[]?> CaptureFrame(int quality);
        void Initialize(string deviceName);
    }
}

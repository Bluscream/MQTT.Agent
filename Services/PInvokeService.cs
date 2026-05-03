using System.Runtime.InteropServices;
using System.Text;

namespace MqttAgent.Services;

public class PInvokeService
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam);
    
    private const uint WM_SETTEXT = 0x000C;

    private const string PipePath = @"\\.\pipe\";

    public List<string> ListPInvokePipes()
    {
        // Use the same method as named pipes since they're the same thing
        // This is just an alternative way to access them
        try
        {
            return Directory.GetFiles(PipePath)
                .Select(pipe => pipe.Replace(PipePath, ""))
                .ToList();
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to list P/Invoke pipes: {ex.Message}");
        }
    }

    public string SendPInvokeMessage(string target, string message)
    {
        try
        {
            var hWnd = FindWindowByTarget(target);
            if (hWnd == IntPtr.Zero)
            {
                throw new Exception($"Window '{target}' not found");
            }

            var sb = new StringBuilder(message);
            var result = SendMessage(hWnd, WM_SETTEXT, IntPtr.Zero, sb);
            
            if (result == IntPtr.Zero)
            {
                throw new Exception($"Failed to send message to window '{target}'");
            }
            
            return $"Message sent to window '{target}' successfully";
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to send P/Invoke message: {ex.Message}");
        }
    }

    private IntPtr FindWindowByTarget(string target)
    {
        var hWnd = FindWindow(target, null);
        return hWnd != IntPtr.Zero ? hWnd : FindWindow(null, target);
    }
}

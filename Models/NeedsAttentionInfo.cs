namespace MqttAgent.Models;

public class NeedsAttentionInfo
{
    public string? WindowName { get; set; }
    public string? ProcessName { get; set; }
    public int ProcessId { get; set; }
    public string? CommandLine { get; set; }
    public string? ClassName { get; set; }
}

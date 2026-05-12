using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MqttAgent.Models;

public class NotifyRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "Notification";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "toast"; // toast, messagebox, banner

    [JsonPropertyName("msgbox_type")]
    public string MessageBoxType { get; set; } = "MB_OK";

    [JsonPropertyName("msgbox_icon")]
    public string MessageBoxIcon { get; set; } = "MB_ICONINFORMATION";

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 0;

    [JsonPropertyName("data")]
    public NotificationData? Data { get; set; }
}

public class StartProcessRequest
{
    [JsonPropertyName("executable")]
    public string Executable { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    [JsonPropertyName("as_user")]
    public string? AsUser { get; set; }

    [JsonPropertyName("elevated")]
    public bool Elevated { get; set; }

    [JsonPropertyName("wait_for_exit")]
    public bool WaitForExit { get; set; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 30000;
}

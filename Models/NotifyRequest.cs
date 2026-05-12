using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MqttAgent.Models;

public class NotifyRequest
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "Notification";

    [JsonPropertyName("heading")]
    public string? Heading { get; set; }

    [JsonPropertyName("footer")]
    public string? Footer { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("checkbox")]
    public string? Checkbox { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "toast";

    [JsonPropertyName("msgbox_type")]
    public string MessageBoxType { get; set; } = "ok";

    [JsonPropertyName("msgbox_icon")]
    public string MessageBoxIcon { get; set; } = "info";

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 0;

    [JsonPropertyName("classic")]
    public bool Classic { get; set; }

    [JsonPropertyName("callback")]
    public string? Callback { get; set; }

    [JsonPropertyName("flash")]
    public bool Flash { get; set; }

    [JsonPropertyName("ding")]
    public bool Ding { get; set; }

    [JsonPropertyName("toast")]
    public bool? UseToast { get; set; }

    [JsonPropertyName("messagebox")]
    public bool? UseMessageBox { get; set; }

    [JsonPropertyName("banner")]
    public bool? UseBanner { get; set; }

    [JsonPropertyName("xsoverlay")]
    public bool? UseXSOverlay { get; set; }

    [JsonPropertyName("ovrtoolkit")]
    public bool? UseOVRToolkit { get; set; }

    [JsonPropertyName("banner_position")]
    public string? BannerPosition { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

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

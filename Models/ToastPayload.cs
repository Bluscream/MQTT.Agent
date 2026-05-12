using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MqttAgent.Models;

public class NotificationAction
{
    public string Action { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
}

public class NotificationInput
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}

public class NotificationData
{
    public const string NoAction = "noAction";
    public const string ImportanceHigh = "high";

    public int Duration { get; set; } = 0;
    public string Image { get; set; } = string.Empty;
    public string ClickAction { get; set; } = NoAction;
    public string Tag { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    [JsonPropertyName("icon_url")]
    public string IconUrl { get; set; } = string.Empty;
    public bool Sticky { get; set; }
    public string Importance { get; set; } = string.Empty;

    public List<NotificationAction> Actions { get; set; } = new List<NotificationAction>();
    public List<NotificationInput> Inputs { get; set; } = new List<NotificationInput>();
}

public class ToastPayload
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public NotificationData? Data { get; set; }

    [JsonPropertyName("use_messagebox")]
    public bool UseMessageBox { get; set; }

    [JsonPropertyName("use_banner")]
    public bool UseBanner { get; set; }

    [JsonPropertyName("banner_position")]
    public string? BannerPosition { get; set; }

    [JsonPropertyName("heading")]
    public string? Heading { get; set; }

    [JsonPropertyName("footer")]
    public string? Footer { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }

    [JsonPropertyName("checkbox")]
    public string? Checkbox { get; set; }

    [JsonPropertyName("type")]
    public string? MessageBoxType { get; set; }

    [JsonPropertyName("icon")]
    public string? MessageBoxIcon { get; set; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; }

    [JsonPropertyName("classic")]
    public bool Classic { get; set; }

    [JsonPropertyName("callback")]
    public string? Callback { get; set; }

    [JsonPropertyName("flash")]
    public bool Flash { get; set; }

    [JsonPropertyName("ding")]
    public bool Ding { get; set; }

    [JsonPropertyName("use_xsoverlay")]
    public bool UseXSOverlay { get; set; }

    [JsonPropertyName("use_ovrtoolkit")]
    public bool UseOVRToolkit { get; set; }
}

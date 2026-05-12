using System;
using MqttAgent.Utils;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;
using MqttAgent.Models;
using NotificationData = MqttAgent.Models.NotificationData;
using Modern_Windows_Message_Box_Generator.CLI;
using SoundSwitch.Banner;
using System.Drawing;

namespace MqttAgent.Services;

public class NotificationReceiverService : IHostedService
{
    private readonly IMqttManager _mqttManager;
    private readonly ILogger<NotificationReceiverService> _logger;
    private string _machineName;
    private string _mqttTopic;

    public NotificationReceiverService(IMqttManager mqttManager, ILogger<NotificationReceiverService> logger)
    {
        _mqttManager = mqttManager;
        _logger = logger;
        _machineName = Global.SafeMachineName;
        _mqttTopic = $"homeassistant/notify/{_machineName}/command";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttManager.SubscribeAsync(_mqttTopic, HandleMessageAsync);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            var payloadString = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            _logger.LogInformation("Received notification payload: {Payload}", payloadString);

            ToastPayload? payload = null;
            try
            {
                payload = JsonSerializer.Deserialize<ToastPayload>(payloadString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                payload = new ToastPayload { Message = payloadString, Title = "Notification", Data = new NotificationData() };
            }

            if (payload != null)
            {
                if (payload.Data == null) payload.Data = new NotificationData();

                if (payload.Message == "clear_notification")
                {
                    if (!string.IsNullOrWhiteSpace(payload.Data.Tag) && !string.IsNullOrWhiteSpace(payload.Data.Group))
                        ToastNotificationManagerCompat.History.Remove(payload.Data.Tag, payload.Data.Group);
                    else if (!string.IsNullOrWhiteSpace(payload.Data.Tag))
                        ToastNotificationManagerCompat.History.Remove(payload.Data.Tag);
                    else
                        ToastNotificationManagerCompat.History.Clear();
                    
                    return Task.CompletedTask;
                }

                if (payload.UseMessageBox || payload.UseBanner || payload.UseXSOverlay || payload.UseOVRToolkit)
                {
                    _logger.LogInformation("Processing enhanced notification (MB: {MB}, Banner: {Banner}, XS: {XS}, OVR: {OVR})", 
                        payload.UseMessageBox, payload.UseBanner, payload.UseXSOverlay, payload.UseOVRToolkit);
                    
                    var args = new List<string>
                    {
                        "-t", payload.Title ?? "Notification",
                        "-m", payload.Message ?? ""
                    };

                    if (payload.UseMessageBox) args.Add("--messagebox");
                    if (payload.UseBanner) args.Add("--banner");
                    if (payload.UseXSOverlay) args.Add("--xsoverlay");
                    if (payload.UseOVRToolkit) args.Add("--ovrtoolkit");

                    if (!string.IsNullOrEmpty(payload.Heading)) { args.Add("--heading"); args.Add(payload.Heading); }
                    if (!string.IsNullOrEmpty(payload.Footer)) { args.Add("--footer"); args.Add(payload.Footer); }
                    if (!string.IsNullOrEmpty(payload.Details)) { args.Add("--details"); args.Add(payload.Details); }
                    if (!string.IsNullOrEmpty(payload.Checkbox)) { args.Add("--checkbox"); args.Add(payload.Checkbox); }
                    if (!string.IsNullOrEmpty(payload.MessageBoxType)) { args.Add("--type"); args.Add(payload.MessageBoxType); }
                    if (!string.IsNullOrEmpty(payload.MessageBoxIcon)) { args.Add("--icon"); args.Add(payload.MessageBoxIcon); }
                    if (payload.Timeout > 0) { args.Add("--timeout"); args.Add(payload.Timeout.ToString()); }
                    if (payload.Classic) args.Add("--classic");
                    if (!string.IsNullOrEmpty(payload.Callback)) { args.Add("--callback"); args.Add(payload.Callback); }
                    if (payload.Flash) args.Add("--flash");
                    if (payload.Ding) args.Add("--ding");
                    
                    if (!string.IsNullOrEmpty(payload.BannerPosition)) { args.Add("--pos"); args.Add(payload.BannerPosition); }
                    if (!string.IsNullOrEmpty(payload.Data?.Image)) { args.Add("--image"); args.Add(payload.Data.Image); }
                    
                    if (payload.Data?.Duration > 0) { args.Add("--duration"); args.Add(payload.Data.Duration.ToString()); }
                    else if (payload.Timeout > 0) { args.Add("--duration"); args.Add((payload.Timeout / 1000).ToString()); }

                    // Run in a separate task so it doesn't block MQTT processing
                    _ = Task.Run(async () => {
                        try {
                            await Modern_Windows_Message_Box_Generator.CLI.Program.Main(args.ToArray());
                        } catch (Exception ex) {
                            _logger.LogError(ex, "Error executing enhanced notification logic");
                        }
                    });
                }

                var builder = new ToastContentBuilder()
                    .AddText(payload.Title ?? "Home Assistant")
                    .AddText(payload.Message ?? "");

                if (payload.Data?.ClickAction != NotificationData.NoAction && !string.IsNullOrWhiteSpace(payload.Data?.ClickAction))
                    builder.AddArgument("action", payload.Data.ClickAction);

                if (!string.IsNullOrWhiteSpace(payload.Data?.Image) && Uri.TryCreate(payload.Data.Image, UriKind.Absolute, out Uri? imageUrl))
                    builder.AddHeroImage(imageUrl);

                if (!string.IsNullOrWhiteSpace(payload.Data?.IconUrl) && Uri.TryCreate(payload.Data.IconUrl, UriKind.Absolute, out Uri? iconUrl))
                    builder.AddAppLogoOverride(iconUrl, ToastGenericAppLogoCrop.Default);

                if (payload.Data?.Actions != null && payload.Data.Actions.Count > 0)
                {
                    foreach (var action in payload.Data.Actions)
                    {
                        if (string.IsNullOrEmpty(action.Action)) continue;
                        
                        var button = new ToastButton().SetContent(action.Title).AddArgument("action", action.Action);
                        if (!string.IsNullOrWhiteSpace(action.Uri)) button.AddArgument("uri", action.Uri);
                        builder.AddButton(button);
                    }
                }

                if (payload.Data?.Inputs != null && payload.Data.Inputs.Count > 0)
                {
                    foreach (var input in payload.Data.Inputs)
                    {
                        if (string.IsNullOrEmpty(input.Id)) continue;
                        builder.AddInputTextBox(input.Id, input.Text, input.Title);
                    }
                }

                if (payload.Data?.Sticky == true) builder.SetToastScenario(ToastScenario.Reminder);
                else if (payload.Data?.Importance == NotificationData.ImportanceHigh) builder.SetToastScenario(ToastScenario.Alarm);

                var toast = builder.GetToastContent();
                var notification = new ToastNotification(toast.GetXml());

                if (!string.IsNullOrWhiteSpace(payload.Data?.Tag)) notification.Tag = payload.Data.Tag;
                if (!string.IsNullOrWhiteSpace(payload.Data?.Group)) notification.Group = payload.Data.Group;

                if (payload.Data?.Duration > 0)
                    notification.ExpirationTime = DateTimeOffset.Now.AddSeconds(payload.Data.Duration);

                ToastNotificationManagerCompat.CreateToastNotifier().Show(notification);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling MQTT notification message");
        }
        
        return Task.CompletedTask;
    }
}

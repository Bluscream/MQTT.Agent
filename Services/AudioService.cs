using System;
using System.Collections.Generic;
using System.Text.Json;
using NAudio.CoreAudioApi;
using System.Linq;
using MqttAgent.Utils;

namespace MqttAgent.Services;

public class AudioService
{
    public string ListAudioDevices()
    {
        var devicesList = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        void AddDevices(DataFlow flow)
        {
            try {
                var collection = enumerator.EnumerateAudioEndPoints(flow, DeviceState.All);
                for (int i = 0; i < collection.Count; i++)
                {
                    try {
                        var device = collection[i];
                        float volume = 0;
                        bool muted = false;

                        if (device.State == DeviceState.Active)
                        {
                            try {
                                volume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                                muted = device.AudioEndpointVolume.Mute;
                            } catch { }
                        }

                        string name = "Unknown";
                        try { name = device.FriendlyName; } catch { }
                        
                        string interfaceName = "";
                        try { interfaceName = device.DeviceFriendlyName; } catch { }

                        string deviceClass = "";
                        try {
                            if (device.Properties.Contains(PropertyKeys.PKEY_Device_FriendlyName)) {
                                deviceClass = device.Properties[PropertyKeys.PKEY_Device_FriendlyName].Value?.ToString() ?? "";
                            }
                        } catch { }

                        devicesList.Add(new AudioDeviceInfo
                        {
                            ID = device.ID,
                            Name = name,
                            InterfaceName = interfaceName,
                            State = device.State.ToString(),
                            Type = flow == DataFlow.Render ? "Render" : "Capture",
                            Volume = (int)(volume * 100),
                            Muted = muted,
                            IconPath = "",
                            DeviceClass = deviceClass
                        });
                    } catch (Exception ex) {
                        // Skip problematic device
                        Console.Error.WriteLine($"Skipping audio device: {ex.Message}");
                    }
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"Failed to enumerate {flow} endpoints: {ex.Message}");
            }
        }

        AddDevices(DataFlow.Render);
        AddDevices(DataFlow.Capture);

        return JsonSerializer.Serialize(devicesList); // Compact JSON
    }

    public string SetAudioVolume(string identifier, int volume)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;

        try
        {
            device = enumerator.GetDevice(identifier);
        }
        catch
        {
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active);
            for (int i = 0; i < collection.Count; i++)
            {
                var dev = collection[i];
                if (dev.FriendlyName.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                {
                    device = dev;
                    break;
                }
            }
        }

        if (device == null || device.State != DeviceState.Active)
            return $"Device '{identifier}' not found or not active.";

        try {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = volume / 100f;
            return $"Volume for '{identifier}' set to {volume}%.";
        } catch (Exception ex) {
            return $"Failed to set volume for '{identifier}': {ex.Message}";
        }
    }

    public string ToggleAudioDevice(string identifier, bool enable)
    {
        try
        {
            var action = enable ? "Enable" : "Disable";
            string? instanceId = null;

            // Search for the PnP device matching the friendly name or ID
            // We use WMI to find the device because NAudio IDs don't always match PnP Instance IDs directly
            using (var searcher = new System.Management.ManagementObjectSearcher(@"root\CIMV2", $"SELECT DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%{identifier.Replace("'", "''")}%' OR DeviceID LIKE '%{identifier.Replace("'", "''")}%'"))
            {
                var obj = searcher.Get().Cast<System.Management.ManagementObject>().FirstOrDefault();
                instanceId = obj?["DeviceID"]?.ToString();
            }

            if (string.IsNullOrEmpty(instanceId))
            {
                return $"Could not find audio device matching '{identifier}'.";
            }

            bool success = PnpHelper.SetDeviceState(instanceId, enable);

            if (success)
            {
                return $"Audio device '{identifier}' {action}d successfully.";
            }
            else
            {
                return $"Failed to {action} audio device '{identifier}' (Native error).";
            }
        }
        catch (Exception ex)
        {
            return $"Failed to toggle audio device: {ex.Message}";
        }
    }
}

public class AudioDeviceInfo
{
    public string ID { get; set; } = "";
    public string Name { get; set; } = "";
    public string InterfaceName { get; set; } = "";
    public string State { get; set; } = "";
    public string Type { get; set; } = "";
    public int Volume { get; set; }
    public bool Muted { get; set; }
    public string IconPath { get; set; } = "";
    public string DeviceClass { get; set; } = "";
}

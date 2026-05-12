using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MqttAgent.Utils;

namespace MqttAgent.Services
{
    public interface IDiscoveryService
    {
        Task PublishDiscoveryAsync();
    }

    public class DiscoveryService : IDiscoveryService
    {
        private readonly ILogger<DiscoveryService> _logger;
        private readonly IMqttManager _mqtt;

        public DiscoveryService(ILogger<DiscoveryService> logger, IMqttManager mqtt)
        {
            _logger = logger;
            _mqtt = mqtt;
        }

        public async Task PublishDiscoveryAsync()
        {
            var uniqueId = _mqtt.UniqueId;
            var entityId = _mqtt.EntityId;
            var deviceIdentifier = Environment.MachineName.ToLowerInvariant();
            var machineName = Environment.MachineName;

            var deviceInfo = new
            {
                identifiers = new[] { deviceIdentifier },
                name = machineName,
                manufacturer = "Bluscream",
                model = "MQTT.Agent",
                sw_version = "1.0.0"
            };

            // 1. Status Select
            var statusConfig = new
            {
                name = "Status",
                unique_id = $"{deviceIdentifier}_status",
                object_id = $"{deviceIdentifier}_status",
                state_topic = $"homeassistant/select/{deviceIdentifier}/state",
                command_topic = $"homeassistant/select/{deviceIdentifier}/set",
                json_attributes_topic = $"homeassistant/select/{deviceIdentifier}/attributes",
                options = new[] { 
                    "On", "Locked", "Logged out", "Updating", "Safe Mode", "Idle", "Shutting Down", "Logging In", "Logging Out", "Needs Attention"
                },
                device = deviceInfo
            };

            // 2. Events Sensor
            var eventConfig = new
            {
                name = "Event",
                unique_id = $"{deviceIdentifier}_event",
                object_id = $"{entityId}_event",
                state_topic = $"homeassistant/sensor/{uniqueId}_event/state",
                value_template = "{{ value_json.event }}",
                json_attributes_topic = $"homeassistant/sensor/{uniqueId}_event/state",
                device = deviceInfo,
                icon = "mdi:bell"
            };

            // 3. Block Shutdown Switch
            var safeMachineName = machineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
            var shutdownConfig = new
            {
                name = "Block Shutdown",
                unique_id = $"{deviceIdentifier}_block_shutdown",
                object_id = $"{entityId}_block_shutdown",
                command_topic = $"homeassistant/switch/{safeMachineName}_block_shutdown/set",
                state_topic = $"homeassistant/switch/{safeMachineName}_block_shutdown/state",
                device = deviceInfo
            };

            // 3b. Force Action Switch
            var forceActionConfig = new
            {
                name = "Force Action",
                unique_id = $"{deviceIdentifier}_force_action",
                object_id = $"{entityId}_force_action",
                command_topic = $"homeassistant/switch/{safeMachineName}_force_action/set",
                state_topic = $"homeassistant/switch/{safeMachineName}_force_action/state",
                device = deviceInfo,
                icon = "mdi:lightning-bolt"
            };

            // 4. Notifications (Notify platform)
            var notifyConfig = new
            {
                name = (string?)null,
                unique_id = $"{deviceIdentifier}_notify",
                object_id = $"{entityId}_notify",
                command_topic = $"homeassistant/notify/{safeMachineName}/command",
                device = deviceInfo
            };

            // 5. Power Profile Select
            var powerProfileConfig = new
            {
                name = "Power Profile",
                unique_id = $"{deviceIdentifier}_power_profile",
                object_id = $"{entityId}_power_profile",
                state_topic = $"homeassistant/select/{uniqueId}_power_profile/state",
                command_topic = $"homeassistant/select/{uniqueId}_power_profile/set",
                options = PowerHelper.GetPowerSchemes().Select(s => s.Name).ToArray(),
                device = deviceInfo,
                icon = "mdi:lightning-bolt"
            };

            // 6. Hardware Sensors (Consolidated)
            var cpuConfig = new { name = "CPU", unique_id = $"{deviceIdentifier}_cpu", object_id = $"{deviceIdentifier}_cpu", state_topic = $"homeassistant/sensor/{deviceIdentifier}_cpu/state", json_attributes_topic = $"homeassistant/sensor/{deviceIdentifier}_cpu/attributes", device = deviceInfo, icon = "mdi:cpu-64-bit" };
            var gpuConfig = new { name = "GPU", unique_id = $"{deviceIdentifier}_gpu", object_id = $"{deviceIdentifier}_gpu", state_topic = $"homeassistant/sensor/{deviceIdentifier}_gpu/state", json_attributes_topic = $"homeassistant/sensor/{deviceIdentifier}_gpu/attributes", device = deviceInfo, icon = "mdi:expansion-card" };
            var ramConfig = new { name = "RAM", unique_id = $"{deviceIdentifier}_ram", object_id = $"{deviceIdentifier}_ram", state_topic = $"homeassistant/sensor/{deviceIdentifier}_ram/state", json_attributes_topic = $"homeassistant/sensor/{deviceIdentifier}_ram/attributes", device = deviceInfo, icon = "mdi:memory" };
            var updateIntervalConfig = new { name = "Update Interval", unique_id = $"{deviceIdentifier}_update_interval", object_id = $"{deviceIdentifier}_update_interval", state_topic = $"homeassistant/number/{deviceIdentifier}_update_interval/state", command_topic = $"homeassistant/number/{deviceIdentifier}_update_interval/set", min = 1, max = 3600, step = 1, unit_of_measurement = "s", device = deviceInfo, icon = "mdi:timer-cog" };

            // 7. Buttons for Actions
            var actionTopic = $"homeassistant/action/{safeMachineName}/command";
            var shutdownBtn = new { name = "Shutdown", unique_id = $"{deviceIdentifier}_btn_shutdown", object_id = $"{entityId}_shutdown", command_topic = actionTopic, payload_press = "{\"action\": \"shutdown\"}", device = deviceInfo, device_class = "restart", icon = "mdi:power" };
            var rebootBtn = new { name = "Reboot", unique_id = $"{deviceIdentifier}_btn_reboot", object_id = $"{entityId}_reboot", command_topic = actionTopic, payload_press = "{\"action\": \"reboot\"}", device = deviceInfo, device_class = "restart", icon = "mdi:restart" };
            var lockBtn = new { name = "Lock", unique_id = $"{deviceIdentifier}_btn_lock", object_id = $"{entityId}_lock", command_topic = actionTopic, payload_press = "{\"action\": \"lock\"}", device = deviceInfo, icon = "mdi:lock" };
            var logoffBtn = new { name = "Logoff", unique_id = $"{deviceIdentifier}_btn_logoff", object_id = $"{entityId}_logoff", command_topic = actionTopic, payload_press = "{\"action\": \"logoff\"}", device = deviceInfo, icon = "mdi:logout" };

            _logger.LogInformation("Publishing unified HA discovery for {Device}", deviceIdentifier);
            
            await _mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}/config", JsonSerializer.Serialize(statusConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/sensor/{uniqueId}_event/config", JsonSerializer.Serialize(eventConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/switch/{safeMachineName}_block_shutdown/config", JsonSerializer.Serialize(shutdownConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/switch/{safeMachineName}_force_action/config", JsonSerializer.Serialize(forceActionConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/notify/{safeMachineName}/config", JsonSerializer.Serialize(notifyConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/select/{uniqueId}_power_profile/config", JsonSerializer.Serialize(powerProfileConfig), true);

            await _mqtt.EnqueueAsync($"homeassistant/sensor/{uniqueId}_cpu/config", JsonSerializer.Serialize(cpuConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/sensor/{uniqueId}_gpu/config", JsonSerializer.Serialize(gpuConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/sensor/{uniqueId}_ram/config", JsonSerializer.Serialize(ramConfig), true);
            await _mqtt.EnqueueAsync($"homeassistant/number/{uniqueId}_update_interval/config", JsonSerializer.Serialize(updateIntervalConfig), true);
            
            await _mqtt.EnqueueAsync($"homeassistant/button/{deviceIdentifier}/shutdown/config", JsonSerializer.Serialize(shutdownBtn), true);
            await _mqtt.EnqueueAsync($"homeassistant/button/{deviceIdentifier}/reboot/config", JsonSerializer.Serialize(rebootBtn), true);
            await _mqtt.EnqueueAsync($"homeassistant/button/{deviceIdentifier}/lock/config", JsonSerializer.Serialize(lockBtn), true);
            await _mqtt.EnqueueAsync($"homeassistant/button/{deviceIdentifier}/logoff/config", JsonSerializer.Serialize(logoffBtn), true);
        }
    }
}

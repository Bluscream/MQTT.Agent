using System;
using MqttAgent.Utils;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;

namespace MqttAgent.Services;

/// <summary>
/// Manages a "Force Action" switch via MQTT.
/// When enabled, system actions (shutdown, reboot, logoff) use their forced variants
/// (i.e. bForceAppsClosed=true for shutdown/reboot, EWX_FORCE for logoff).
/// </summary>
public class ForceActionService : IHostedService
{
    private readonly IMqttManager _mqttManager;
    private readonly ILogger<ForceActionService> _logger;
    private bool _forceActions;
    private readonly string _machineName;
    private readonly string _stateTopic;
    private readonly string _commandTopic;

    public ForceActionService(IMqttManager mqttManager, ILogger<ForceActionService> logger)
    {
        _mqttManager = mqttManager;
        _logger = logger;
        _machineName = Global.SafeMachineName;

        _stateTopic = $"homeassistant/switch/{_machineName}_force_action/state";
        _commandTopic = $"homeassistant/switch/{_machineName}_force_action/set";
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _mqttManager.SubscribeAsync(_commandTopic, HandleCommandAsync);
        await PublishStateAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task HandleCommandAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var payload = System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        _forceActions = string.Equals(payload, "ON", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("Force Action set to: {State}", _forceActions);
        await PublishStateAsync();
    }

    private async Task PublishStateAsync()
    {
        var state = _forceActions ? "ON" : "OFF";
        await _mqttManager.EnqueueAsync(_stateTopic, state, true);
    }

    /// <summary>Whether forced actions are currently enabled.</summary>
    public bool IsForceEnabled => _forceActions;

    /// <summary>Toggle force action state programmatically.</summary>
    public async Task SetForceEnabled(bool enabled)
    {
        _forceActions = enabled;
        _logger.LogInformation("Force Action set to: {State}", _forceActions);
        await PublishStateAsync();
    }
}

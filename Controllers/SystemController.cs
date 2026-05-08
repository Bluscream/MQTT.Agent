using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MqttAgent.Services;

namespace MqttAgent.Controllers;

[ApiController]
[Route("api/system")]
[Authorize]
public class SystemController : ControllerBase
{
    private readonly ShutdownBlockerService _blocker;
    private readonly ForceActionService _forceActionService;
    private readonly IMqttManager _mqtt;

    public SystemController(ShutdownBlockerService blocker, ForceActionService forceActionService, IMqttManager mqtt)
    {
        _blocker = blocker;
        _forceActionService = forceActionService;
        _mqtt = mqtt;
    }

    [HttpGet("block-status")]
    public IActionResult GetBlockStatus()
    {
        return Ok(new { enabled = _blocker.IsBlockingEnabled });
    }

    [HttpPost("toggle-block")]
    public async Task<IActionResult> ToggleBlock([FromQuery] bool enabled)
    {
        var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var topic = $"homeassistant/switch/{machineName}_block_shutdown/set";
        var payload = enabled ? "ON" : "OFF";
        await _mqtt.EnqueueAsync(topic, payload, true);
        
        return Ok(new { enabled });
    }

    [HttpGet("force-status")]
    public IActionResult GetForceStatus()
    {
        return Ok(new { enabled = _forceActionService.IsForceEnabled });
    }

    [HttpPost("toggle-force")]
    public async Task<IActionResult> ToggleForce([FromQuery] bool enabled)
    {
        var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var topic = $"homeassistant/switch/{machineName}_force_action/set";
        var payload = enabled ? "ON" : "OFF";
        await _mqtt.EnqueueAsync(topic, payload, true);
        
        return Ok(new { enabled });
    }

    [HttpPost("execute")]
    public async Task<IActionResult> ExecuteAction([FromQuery] string action)
    {
        var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var topic = $"homeassistant/action/{machineName}/command";
        await _mqtt.EnqueueAsync(topic, action, true);
        
        return Ok(new { action });
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MqttAgent.Services;
using System.Text.Json;

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

    [AcceptVerbs("GET", "POST"), Route("toggle-block")]
    public async Task<IActionResult> ToggleBlock([FromQuery] bool? enabled)
    {
        bool finalEnabled = enabled ?? false;
        
        if (enabled == null && Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("enabled", out var prop)) 
                    finalEnabled = prop.ValueKind == JsonValueKind.True;
            } catch { }
        }

        var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var topic = $"homeassistant/switch/{machineName}_block_shutdown/set";
        var payload = finalEnabled ? "ON" : "OFF";
        await _mqtt.EnqueueAsync(topic, payload, true);
        
        return Ok(new { enabled = finalEnabled });
    }

    [HttpGet("force-status")]
    public IActionResult GetForceStatus()
    {
        return Ok(new { enabled = _forceActionService.IsForceEnabled });
    }

    [AcceptVerbs("GET", "POST"), Route("toggle-force")]
    public async Task<IActionResult> ToggleForce([FromQuery] bool? enabled)
    {
        bool finalEnabled = enabled ?? false;

        if (enabled == null && Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("enabled", out var prop)) 
                    finalEnabled = prop.ValueKind == JsonValueKind.True;
            } catch { }
        }

        var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var topic = $"homeassistant/switch/{machineName}_force_action/set";
        var payload = finalEnabled ? "ON" : "OFF";
        await _mqtt.EnqueueAsync(topic, payload, true);
        
        return Ok(new { enabled = finalEnabled });
    }

    [AcceptVerbs("GET", "POST"), Route("execute")]
    public async Task<IActionResult> ExecuteAction([FromQuery] string? action)
    {
        string? finalAction = action;

        if (string.IsNullOrEmpty(finalAction) && Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("action", out var prop))
                {
                    finalAction = prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
                }
            } catch { }
        }

        if (string.IsNullOrEmpty(finalAction)) return BadRequest("Action is required.");

        var machineName = Environment.MachineName.ToLowerInvariant().Replace(" ", "_").Replace("-", "_");
        var topic = $"homeassistant/action/{machineName}/command";
        await _mqtt.EnqueueAsync(topic, finalAction, true);
        
        return Ok(new { action = finalAction });
    }
}

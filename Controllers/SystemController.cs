using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MqttAgent.Services;
using MqttAgent.Models;
using System.Text.Json;
using System.Diagnostics;
using System;
using MqttAgent.Utils;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MqttAgent.Controllers;

[ApiController]
[Route("api/system")]
[Authorize]
public class SystemController : ControllerBase
{
    private readonly ShutdownBlockerService _blocker;
    private readonly ForceActionService _forceActionService;
    private readonly IMqttManager _mqtt;
    private readonly ProcessService _processService;
    private readonly ILogger<SystemController> _logger;

    public SystemController(ShutdownBlockerService blocker, ForceActionService forceActionService, IMqttManager mqtt, ProcessService processService, ILogger<SystemController> logger)
    {
        _blocker = blocker;
        _forceActionService = forceActionService;
        _mqtt = mqtt;
        _processService = processService;
        _logger = logger;
    }

    [HttpPost("notify")]
    public async Task<IActionResult> Notify([FromBody] NotifyRequest request)
    {
        if (string.IsNullOrEmpty(request.Message)) return BadRequest("Message is required.");

        try
        {
            if (request.Type.Equals("messagebox", StringComparison.OrdinalIgnoreCase))
            {
                var sessionId = _processService.GetActiveConsoleSessionId();
                var args = $"--messagebox --title \"{request.Title}\" --message \"{request.Message}\" --type \"{request.MessageBoxType}\" --icon \"{request.MessageBoxIcon}\" --timeout {request.Timeout}";
                await _processService.StartProcess(Process.GetCurrentProcess().MainModule?.FileName ?? "MqttAgent.exe", args, asUser: sessionId.ToString());
            }
            else if (request.Type.Equals("banner", StringComparison.OrdinalIgnoreCase))
            {
                var sessionId = _processService.GetActiveConsoleSessionId();
                var args = $"--banner --message \"{request.Message}\"";
                await _processService.StartProcess(Process.GetCurrentProcess().MainModule?.FileName ?? "MqttAgent.exe", args, asUser: sessionId.ToString());
            }
            else
            {
                // Default to Toast via MQTT topic to leverage existing logic
                var machineName = Global.SafeMachineName;
                var topic = $"homeassistant/notify/{machineName}/command";
                var payload = JsonSerializer.Serialize(new ToastPayload
                {
                    Title = request.Title,
                    Message = request.Message,
                    Data = request.Data
                });
                await _mqtt.EnqueueAsync(topic, payload, false);
            }

            return Ok(new { status = "success" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing notify request");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("start-process")]
    public async Task<IActionResult> StartProcess([FromBody] StartProcessRequest request)
    {
        if (string.IsNullOrEmpty(request.Executable)) return BadRequest("Executable is required.");

        try
        {
            var result = await _processService.StartProcess(
                request.Executable,
                request.Arguments,
                request.WaitForExit,
                request.Timeout,
                asUser: request.AsUser,
                elevated: request.Elevated);

            return Ok(new { status = "success", result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting process");
            return StatusCode(500, new { error = ex.Message });
        }
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

        var machineName = Global.SafeMachineName;
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

        var machineName = Global.SafeMachineName;
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

        var machineName = Global.SafeMachineName;
        var topic = $"homeassistant/action/{machineName}/command";
        await _mqtt.EnqueueAsync(topic, finalAction, true);
        
        return Ok(new { action = finalAction });
    }
}

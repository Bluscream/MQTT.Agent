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
using System.Collections.Generic;

namespace MqttAgent.Controllers;

[ApiController]
[Route("api")]
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
            var machineName = Global.SafeMachineName;
            
            // Handle multiple notification types based on flags or the 'Type' string
            bool useToast = request.UseToast ?? request.Type.Contains("toast", StringComparison.OrdinalIgnoreCase);
            bool useMessageBox = request.UseMessageBox ?? request.Type.Contains("messagebox", StringComparison.OrdinalIgnoreCase);
            bool useBanner = request.UseBanner ?? request.Type.Contains("banner", StringComparison.OrdinalIgnoreCase);
            bool useXSOverlay = request.UseXSOverlay ?? request.Type.Contains("xsoverlay", StringComparison.OrdinalIgnoreCase);
            bool useOVRToolkit = request.UseOVRToolkit ?? request.Type.Contains("ovrtoolkit", StringComparison.OrdinalIgnoreCase);

            // Default to Toast if nothing specified
            if (!useToast && !useMessageBox && !useBanner && !useXSOverlay && !useOVRToolkit) useToast = true;

            if (useToast)
            {
                var topic = $"homeassistant/notify/{machineName}/command";
                var payload = JsonSerializer.Serialize(new ToastPayload
                {
                    Title = request.Title,
                    Message = request.Message,
                    Data = request.Data
                });
                await _mqtt.EnqueueAsync(topic, payload, false);
            }

            if (useMessageBox || useBanner || useXSOverlay || useOVRToolkit)
            {
                var sessionId = _processService.GetActiveConsoleSessionId();
                var argsList = new List<string> { "--title", $"\"{request.Title}\"", "--message", $"\"{request.Message}\"" };
                
                if (!string.IsNullOrEmpty(request.Heading)) argsList.AddRange(new[] { "--heading", $"\"{request.Heading}\"" });
                if (!string.IsNullOrEmpty(request.Footer)) argsList.AddRange(new[] { "--footer", $"\"{request.Footer}\"" });
                if (!string.IsNullOrEmpty(request.Details)) argsList.AddRange(new[] { "--details", $"\"{request.Details}\"" });
                if (!string.IsNullOrEmpty(request.Checkbox)) argsList.AddRange(new[] { "--checkbox", $"\"{request.Checkbox}\"" });
                if (!string.IsNullOrEmpty(request.MessageBoxType)) argsList.AddRange(new[] { "--type", request.MessageBoxType });
                if (!string.IsNullOrEmpty(request.MessageBoxIcon)) argsList.AddRange(new[] { "--icon", request.MessageBoxIcon });
                if (request.Timeout > 0) argsList.AddRange(new[] { "--timeout", request.Timeout.ToString() });
                if (request.Classic) argsList.Add("--classic");
                if (!string.IsNullOrEmpty(request.Callback)) argsList.AddRange(new[] { "--callback", $"\"{request.Callback}\"" });
                if (request.Flash) argsList.Add("--flash");
                if (request.Ding) argsList.Add("--ding");
                
                // Add banner specific flags
                if (useBanner) 
                {
                    argsList.Add("--banner");
                    if (!string.IsNullOrEmpty(request.BannerPosition)) argsList.AddRange(new[] { "--pos", request.BannerPosition });
                    // Duration for banner might map from timeout
                    if (request.Timeout > 0) argsList.AddRange(new[] { "--duration", (request.Timeout / 1000).ToString() });
                }

                if (!string.IsNullOrEmpty(request.Image)) argsList.AddRange(new[] { "--image", $"\"{request.Image}\"" });
                // Also support Image from Data
                else if (request.Data?.Image != null) argsList.AddRange(new[] { "--image", $"\"{request.Data.Image}\"" });

                if (useMessageBox) argsList.Add("--messagebox");
                if (useXSOverlay) argsList.Add("--xsoverlay");
                if (useOVRToolkit) argsList.Add("--ovrtoolkit");

                var args = string.Join(" ", argsList);
                await _processService.StartProcess(Process.GetCurrentProcess().MainModule?.FileName ?? "MqttAgent.exe", args, asUser: sessionId.ToString());
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

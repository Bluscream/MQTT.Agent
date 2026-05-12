using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MqttAgent.Services;
using System.Text.Json;

namespace MqttAgent.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class DeviceController : ControllerBase
{
    private readonly DeviceService _deviceService;

    public DeviceController(DeviceService deviceService)
    {
        _deviceService = deviceService;
    }

    [HttpGet("device-list")]
    public IActionResult ListDevices([FromQuery] string[]? categories)
    {
        var result = _deviceService.ListDevices(categories);
        return Ok(result);
    }

    [AcceptVerbs("GET", "POST"), Route("device-enable")]
    public async Task<IActionResult> EnableDevices([FromQuery] string? pattern)
    {
        var finalPattern = await GetPattern(pattern);
        if (string.IsNullOrEmpty(finalPattern)) return BadRequest("Pattern is required.");
        var result = await _deviceService.ToggleDevices(new[] { finalPattern }, null);
        return Ok(result);
    }

    [AcceptVerbs("GET", "POST"), Route("device-disable")]
    public async Task<IActionResult> DisableDevices([FromQuery] string? pattern)
    {
        var finalPattern = await GetPattern(pattern);
        if (string.IsNullOrEmpty(finalPattern)) return BadRequest("Pattern is required.");
        var result = await _deviceService.ToggleDevices(null, new[] { finalPattern });
        return Ok(result);
    }

    [AcceptVerbs("GET", "POST"), Route("device-restart")]
    public async Task<IActionResult> RestartDevices([FromQuery] string? pattern)
    {
        var finalPattern = await GetPattern(pattern);
        if (string.IsNullOrEmpty(finalPattern)) return BadRequest("Pattern is required.");
        var result = await _deviceService.ToggleDevices(new[] { finalPattern }, new[] { finalPattern });
        return Ok(result);
    }

    private async Task<string?> GetPattern(string? queryPattern)
    {
        if (!string.IsNullOrEmpty(queryPattern)) return queryPattern;
        if (Request.HasJsonContentType())
        {
            try {
                var body = await Request.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("pattern", out var prop)) return prop.GetString();
            } catch { }
        }
        return null;
    }
}

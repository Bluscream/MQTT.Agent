using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MqttAgent.Services;

namespace MqttAgent.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ScreenshotController : ControllerBase
{
    private readonly ScreenshotService _screenshotService;
    private readonly MultiMonitorToolService _monitorService;

    public ScreenshotController(ScreenshotService screenshotService, MultiMonitorToolService monitorService)
    {
        _screenshotService = screenshotService;
        _monitorService = monitorService;
    }

    [HttpGet]
    public async Task<IActionResult> GetScreenshot([FromQuery] string desktop = "Default", [FromQuery] int quality = 75, [FromQuery] string display = "all", [FromQuery] string format = "png")
    {
        try
        {
            var result = await _screenshotService.CaptureScreenshot(desktop, quality, display, format);
            if (result != null && result.StartsWith("data:"))
            {
                var parts = result.Split(',');
                var mimePart = parts[0];
                var mimeType = mimePart.Contains("image/png") ? "image/png" : "image/jpeg";
                var base64Data = parts[1];
                var imageBytes = Convert.FromBase64String(base64Data);
                return File(imageBytes, mimeType);
            }
            return BadRequest(new { error = result ?? "Unknown error" });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("screens")]
    public async Task<IActionResult> GetScreens()
    {
        var screens = await _screenshotService.ListScreens();
        return Ok(screens);
    }
}

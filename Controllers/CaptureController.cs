using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MqttAgent.Services;

namespace MqttAgent.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class CaptureController : ControllerBase
{
    private readonly ScreenshotService _screenshotService;

    public CaptureController(ScreenshotService screenshotService)
    {
        _screenshotService = screenshotService;
    }

    [HttpGet("screenshot")]
    public async Task<IActionResult> GetScreenshot(
        [FromQuery] string desktop = "Default", 
        [FromQuery] int quality = 75, 
        [FromQuery] string display = "all",
        [FromQuery] string? screen = null,
        [FromQuery] string format = "png")
    {
        display = screen ?? display;
        try
        {
            var bytes = await _screenshotService.CaptureScreenshot(desktop, quality, display, format);
            if (bytes != null)
            {
                var mimeType = format.ToLower().Contains("png") ? "image/png" : "image/jpeg";
                return File(bytes, mimeType);
            }
            return BadRequest(new { error = "Capture failed." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("stream")]
    public async Task GetStream(
        [FromQuery] string desktop = "Default", 
        [FromQuery] int quality = 50, 
        [FromQuery] string display = "all",
        [FromQuery] string? screen = null,
        [FromQuery] int fps = 10,
        [FromQuery] string format = "mjpeg")
    {
        display = screen ?? display;
        Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        var ct = HttpContext.RequestAborted;

        try
        {
            await _screenshotService.StartStreamingProcess(display, quality, fps, Response.Body, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[CaptureController] Stream error: {ex.Message}");
        }
    }
}

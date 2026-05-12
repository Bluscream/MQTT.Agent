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
            var bytes = await _screenshotService.CaptureScreenshot(desktop, quality, display, format);
            if (bytes != null)
            {
                var mimeType = format.ToLower().Contains("png") ? "image/png" : "image/jpeg";
                return File(bytes, mimeType);
            }
            return BadRequest(new { error = "Screenshot capture failed." });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("stream")]
    public async Task GetStream([FromQuery] string desktop = "Default", [FromQuery] int quality = 50, [FromQuery] string display = "all", [FromQuery] int fps = 2)
    {
        Response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
        var cancellationToken = HttpContext.RequestAborted;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytes = await _screenshotService.CaptureScreenshot(desktop, quality, display, "jpg");
                if (bytes != null)
                {
                    await Response.Body.WriteAsync(System.Text.Encoding.ASCII.GetBytes("\r\n--frame\r\n"), cancellationToken);
                    await Response.Body.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"Content-Type: image/jpeg\r\nContent-Length: {bytes.Length}\r\n\r\n"), cancellationToken);
                    await Response.Body.WriteAsync(bytes, cancellationToken);
                    await Response.Body.FlushAsync(cancellationToken);
                }
                
                await Task.Delay(1000 / Math.Max(1, fps), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScreenshotController] Stream error: {ex.Message}");
        }
    }

    [HttpGet("screens")]
    public async Task<IActionResult> GetScreens()
    {
        var screens = await _screenshotService.ListScreens();
        return Ok(screens);
    }
}

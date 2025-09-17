namespace Abyss.Components.Controllers.Middleware;

public class BadRequestExceptionMiddleware(RequestDelegate next, ILogger<BadRequestExceptionMiddleware> logger)
{
    public async System.Threading.Tasks.Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Bad Request");
        }
    }
}

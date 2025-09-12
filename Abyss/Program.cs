using System.Threading.RateLimiting;
using Abyss.Components.Controllers.Task;
using Abyss.Components.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace Abyss;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddAuthorization();
        builder.Services.AddMemoryCache();
        builder.Services.AddControllers();
        builder.Services.AddSingleton<ConfigureService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<ResourceService>();
        builder.Services.AddSingleton<TaskController>();
        builder.Services.AddSingleton<TaskService>();
        builder.Services.AddHostedService<AbyssService>();
        
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("Fixed", policyOptions =>
            {
                policyOptions.Window = TimeSpan.FromSeconds(30);
                policyOptions.PermitLimit = 10;
                policyOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                policyOptions.QueueLimit = 0;
            });

            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsync("Too many requests. Please try later.", token);
            };
        });

        builder.Services.BuildServiceProvider().GetRequiredService<UserService>();

        var app = builder.Build();

        // app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        
        app.UseRateLimiter();
        app.Run();
    }
}
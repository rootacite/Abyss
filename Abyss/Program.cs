
using System.Threading.RateLimiting;
using Abyss.Components.Controllers.Middleware;
using Abyss.Components.Controllers.Task;
using Abyss.Components.Services.Admin;
using Abyss.Components.Services.Admin.Attributes;
using Abyss.Components.Services.Admin.Modules;
using Abyss.Components.Services.Media;
using Abyss.Components.Services.Misc;
using Abyss.Components.Services.Security;

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
        builder.Services.AddSingleton<ResourceDatabaseService>();
        builder.Services.AddSingleton<ConfigureService>();
        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<ResourceService>();
        builder.Services.AddSingleton<TaskController>();
        builder.Services.AddSingleton<TaskService>();
        builder.Services.AddSingleton<IndexService>();
        builder.Services.AddSingleton<VideoService>();
        builder.Services.AddSingleton<ComicService>();
        builder.Services.AddHostedService<AbyssService>();
        builder.Services.AddHostedService<CtlService>();

        foreach (var t in ModuleAttribute.Modules)
        {
            builder.Services.AddTransient(t);
        }
        
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
        
        var app = builder.Build();

        // app.UseHttpsRedirection();
        app.UseMiddleware<BadRequestExceptionMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        
        app.UseRateLimiter();
        app.Run();
    }
}
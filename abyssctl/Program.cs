

namespace abyssctl;


static class Program
{
    static async Task<int> Main(string[] args)
    {
        var app = new App.App();
        return await app.RunAsync(args);
    }
}
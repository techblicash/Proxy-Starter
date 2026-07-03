using System;
using ProxyStarter.App.Services;
using Velopack;

namespace ProxyStarter.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "Startup: Velopack");
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

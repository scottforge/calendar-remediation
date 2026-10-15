using System.Net;
using System.Net.Sockets;
using System.Globalization;
using CalendarRemediation.Host.Authentication;
using CalendarRemediation.Host.Components;

var builder = WebApplication.CreateBuilder(args);

var port = FindAvailablePort(5009);
builder.Configuration["LocalConnection:Port"] = port.ToString(CultureInfo.InvariantCulture);
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
builder.Services.AddLocalConnection(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseLocalConnectionGuard();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapLocalConnection();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static int FindAvailablePort(int preferredPort)
{
    try
    {
        using var preferredListener = new TcpListener(IPAddress.Loopback, preferredPort);
        preferredListener.Start();
        return preferredPort;
    }
    catch (SocketException)
    {
        using var fallbackListener = new TcpListener(IPAddress.Loopback, 0);
        fallbackListener.Start();
        return ((IPEndPoint)fallbackListener.LocalEndpoint).Port;
    }
}

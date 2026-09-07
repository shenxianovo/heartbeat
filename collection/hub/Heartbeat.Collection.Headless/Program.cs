using Heartbeat.Collection.Headless;
using Serilog;

var configPath = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "heartbeat-headless.json");
var options = HeadlessFleetOptions.Load(configPath);
options.Validate();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls(options.ListenUrl);
    builder.Services.AddHeadlessCollectors(options);
    builder.Services.AddHeadlessManagement(options.Management);

    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHeadlessManagement();

    await app.RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}

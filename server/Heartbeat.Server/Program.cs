using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Heartbeat.Server.Authentication;
using Heartbeat.Server.Data;
using Heartbeat.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
);
builder.Services.AddScoped<UsageService>();

builder.Services.AddAuthentication(ApiKeyDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

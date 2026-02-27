using client.Models;
using Microsoft.Extensions.Configuration;

namespace client.Services
{
    public class ConfigService
    {
        public Config LoadConfig()
        {
            var env = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env}.json", optional: true, reloadOnChange: true)
                .Build();

            return configuration.Get<Config>()!;
        }
    }
}

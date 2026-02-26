using client.Models;
using Microsoft.Extensions.Configuration;

namespace client.Services
{
    public class ConfigService
    {
        public Config LoadConfig(string configFilePath = "appsettings.json")
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile(configFilePath, optional: true, reloadOnChange: true)
                .Build();

            return configuration.Get<Config>()!;
        }

    }
}

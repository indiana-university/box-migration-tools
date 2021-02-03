using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(boxaccountorchestration.Startup))]

namespace boxaccountorchestration
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddOptions<IBoxAccountConfig>().Configure<IConfiguration>(
                (settings, configuration) => {
                    configuration.GetSection("BoxAccountConfig").Bind(settings);
                });
        }
    }
}
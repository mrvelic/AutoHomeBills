using Mcrio.Configuration.Provider.Docker.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using System.Threading.Tasks;

namespace AutoBills
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using var host = CreateHostBuilder(args).Build();

            await host.RunAsync();
        }

        static IHostBuilder CreateHostBuilder(string[] args) => Host
            .CreateDefaultBuilder(args)
            .ConfigureHostConfiguration(c =>
            {                
                c.AddDockerSecrets();
            })
            .ConfigureServices((host, s) =>
            {
                s.AddOptions();
                s.AddHttpClient();

                s.Configure<QuartzOptions>(host.Configuration.GetSection("Quartz"));
                s.Configure<BillsWorkerOptions>(host.Configuration.GetSection(BillsWorkerOptions.OptionsKey));

                s.AddQuartz(q =>
                {
                    q.UseMicrosoftDependencyInjectionJobFactory(o => o.AllowDefaultConstructor = true);
                    q.UseTimeZoneConverter();
                });
                
                s.AddTransient<BillsJob>();
                s.AddHostedService<BillsWorker>();
            });
    }
}

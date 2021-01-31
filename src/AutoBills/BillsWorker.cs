using Baseline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using System.Threading;
using System.Threading.Tasks;
using TimeZoneConverter;

namespace AutoBills
{
    public interface IBillsWorker : IHostedService
    {
    }

    public class BillsWorker : IBillsWorker
    {
        private readonly ILogger<BillsWorker> _log;
        private readonly IOptions<BillsWorkerOptions> _options;
        private readonly ISchedulerFactory _schedulerFactory;
        private IScheduler _scheduler;

        public BillsWorker(ILogger<BillsWorker> log, IOptions<BillsWorkerOptions> options, ISchedulerFactory schedulerFactory)
        {
            _log = log;
            _options = options;
            _schedulerFactory = schedulerFactory;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _log.LogInformation("Bills worker starting up.");
            _scheduler = await _schedulerFactory.GetScheduler(cancellationToken);

            await _scheduler.Start(cancellationToken);

            var job = JobBuilder.Create<BillsJob>()
                .WithIdentity("billsJob", "jobs")
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity("billsJobTrigger", "jobs")
                .StartNow()
                .WithCronSchedule(_options.Value.CronSchedule, c =>
                {
                    if (_options.Value.CronTimeZone.IsNotEmpty())
                    {
                        c.InTimeZone(TZConvert.GetTimeZoneInfo(_options.Value.CronTimeZone));
                    }
                })
                .Build();

            await _scheduler.ScheduleJob(job, trigger, cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _log.LogInformation("Bills worker shutting down.");
            await _scheduler.Shutdown(cancellationToken);
        }
    }
}

using Autofac;

using Microsoft.Extensions.Configuration;

using Myriad.Cache;
using Myriad.Gateway;
using Myriad.Types;
using Myriad.Rest;

using PluralKit.Core;

using Sentry;

using Serilog;
using Serilog.Core;

namespace GatewayWorker;

public class Init
{
    private static async Task Main(string[] args)
    {
        // set cluster config from Nomad node index env variable
        if (Environment.GetEnvironmentVariable("NOMAD_ALLOC_INDEX") is { } nodeIndex)
            Environment.SetEnvironmentVariable("PluralKit__Bot__Cluster__NodeName", $"pluralkit-{nodeIndex}");

        // Load configuration and run global init stuff
        var config = InitUtils.BuildConfiguration(args).Build();
        InitUtils.InitStatic();

        // init version service
        await BuildInfoService.LoadVersion();

        // Set up DI container and modules
        var services = BuildContainer(config);

        await RunWrapper(services, async ct =>
        {
            var logger = services.Resolve<ILogger>().ForContext<Init>();

            // Initialize Sentry SDK, and make sure it gets dropped at the end

            using var _ = SentrySdk.Init(opts =>
            {
                opts.Dsn = services.Resolve<CoreConfig>().SentryUrl;
                opts.Release = BuildInfoService.FullVersion;
                opts.AutoSessionTracking = true;
                opts.DisableTaskUnobservedTaskExceptionCapture();
            });

            var config = services.Resolve<GatewayWorkerConfig>();
            var coreConfig = services.Resolve<CoreConfig>();

            // initialize Redis
            var redis = services.Resolve<RedisService>();
            await redis.InitAsync(coreConfig);

            var cache = services.Resolve<IDiscordCache>();
            if (cache is RedisDiscordCache)
                await (cache as RedisDiscordCache).InitAsync(coreConfig.RedisAddr);

            logger.Information("Initializing services");
            var worker = services.Resolve<WorkerMainService>();
            await worker.Init();

            // Start the Discord shards themselves (handlers already set up)
            logger.Information("Connecting to Discord");
            await StartCluster(services);

            logger.Information("Connected! All is good (probably).");

            // Lastly, we just... wait. Everything else is handled in the DiscordClient event loop
            try
            {
                await Task.Delay(-1, ct);
            }
            catch (TaskCanceledException)
            {
                // Once the CancellationToken fires, we need to shut stuff down
                // (generally happens given a SIGINT/SIGKILL/Ctrl-C, see calling wrapper)
                await worker.Shutdown();
            }
        });
    }

    private static async Task RunWrapper(IContainer services, Func<CancellationToken, Task> taskFunc)
    {
        // This function does a couple things:
        // - Creates a CancellationToken that'll cancel tasks once needed
        // - Wraps the given function in an exception handler that properly logs errors
        // - Adds a SIGINT (Ctrl-C) listener through Console.CancelKeyPress to gracefully shut down
        // - Adds a SIGTERM (kill, systemctl stop, docker stop) listener through AppDomain.ProcessExit (same as above)
        // todo: move run-clustered.sh to here
        var logger = services.Resolve<ILogger>().ForContext<Init>();

        var shutdown = new TaskCompletionSource<object>();
        var gracefulShutdownCts = new CancellationTokenSource();

        Console.CancelKeyPress += delegate
        {
            // ReSharper disable once AccessToDisposedClosure (will only be hit before the below disposal)
            logger.Information("Received SIGINT/Ctrl-C, attempting graceful shutdown...");
            gracefulShutdownCts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            // This callback is fired on a SIGKILL is sent.
            // The runtime will kill the program as soon as this callback is finished, so we have to
            // block on the shutdown task's completion to ensure everything is sorted by the time this returns.

            // ReSharper disable once AccessToDisposedClosure (it's only disposed after the block)
            logger.Information("Received SIGKILL event, attempting graceful shutdown...");
            gracefulShutdownCts.Cancel();
            var ___ = shutdown.Task.Result; // Blocking! This is the only time it's justified...
        };

        try
        {
            await taskFunc(gracefulShutdownCts.Token);
            logger.Information("Shutdown complete. Have a nice day~");
        }
        catch (Exception e)
        {
            logger.Fatal(e, "Error while running gateway worker");
        }

        // Allow the log buffer to flush properly before exiting
        ((Logger)logger).Dispose();
        await Task.Delay(500);
        shutdown.SetResult(null);
    }

    private static IContainer BuildContainer(IConfiguration config)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance(config);

        // Sentry stuff
        builder.Register(_ => new Scope(null)).AsSelf().InstancePerLifetimeScope();

        // we use "bot" here so local dev doesn't have to copy-paste the Bot section in pluralkit.conf
        builder.RegisterModule(new ConfigModule<GatewayWorkerConfig>("Bot"));
        builder.RegisterModule(new LoggingModule("gateway_worker"));
        builder.RegisterModule(new MetricsModule());

        builder.Register(c =>
        {
            var config = c.Resolve<GatewayWorkerConfig>();
            return new GatewaySettings
            {
                Token = config.Token,
                MaxShardConcurrency = config.MaxShardConcurrency,
                UseRedisRatelimiter = true,
                Intents = GatewayIntent.Guilds |
                          GatewayIntent.DirectMessages |
                          GatewayIntent.DirectMessageReactions |
                          GatewayIntent.GuildEmojis |
                          GatewayIntent.GuildMessages |
                          GatewayIntent.GuildWebhooks |
                          GatewayIntent.GuildMessageReactions |
                          GatewayIntent.MessageContent
            };
        }).AsSelf().SingleInstance();

        builder.Register(c => new DiscordApiClient(
                c.Resolve<GatewayWorkerConfig>().Token,
                c.Resolve<ILogger>(),
                c.Resolve<GatewayWorkerConfig>().DiscordBaseUrl
        )).AsSelf().SingleInstance();

        builder.RegisterType<Cluster>().AsSelf().SingleInstance();
        builder.Register<IDiscordCache>(c =>
        {
            var config = c.Resolve<GatewayWorkerConfig>();

            if (config.UseRedisCache)
                return new RedisDiscordCache(c.Resolve<ILogger>(), config.ClientId);
            return new MemoryDiscordCache(config.ClientId);
        }).AsSelf().SingleInstance();

        builder.RegisterType<RedisService>().AsSelf().SingleInstance();

        builder.RegisterType<WorkerMainService>().AsSelf().SingleInstance();
        builder.RegisterType<ShardInfoService>().AsSelf().SingleInstance();

        return builder.Build();
    }

    private static async Task StartCluster(IComponentContext services)
    {
        var redis = services.Resolve<RedisService>();

        var cluster = services.Resolve<Cluster>();
        var config = services.Resolve<GatewayWorkerConfig>();

        if (config.Cluster != null)
        {
            var info = new GatewayInfo.Bot()
            {
                SessionStartLimit = new()
                {
                    MaxConcurrency = config.MaxShardConcurrency ?? 1,
                },
                Shards = config.Cluster.TotalShards,
                Url = "wss://gateway.discord.gg",
            };

            // For multi-instance deployments, calculate the "span" of shards this node is responsible for
            var totalNodes = config.Cluster.TotalNodes;
            var totalShards = config.Cluster.TotalShards;
            var nodeIndex = config.Cluster.NodeIndex;

            // Should evenly distribute shards even with an uneven amount of nodes
            var shardMin = (int)Math.Round(totalShards * (float)nodeIndex / totalNodes);
            var shardMax = (int)Math.Round(totalShards * (float)(nodeIndex + 1) / totalNodes) - 1;

            await cluster.Start(info.Url, shardMin, shardMax, totalShards, info.SessionStartLimit.MaxConcurrency, redis.Connection);
        }
        else
        {
            var info = await services.Resolve<DiscordApiClient>().GetGatewayBot();
            await cluster.Start(info, redis.Connection);
        }
    }
}
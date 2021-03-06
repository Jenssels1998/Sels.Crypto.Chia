using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sels.Core.Components.Configuration;
using Sels.Core.Contracts.Configuration;
using Sels.Core.Contracts.Factory;
using Sels.Core.Unity.Components.Containers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sels.Core.Extensions;
using Sels.Core.Extensions.Conversion;
using Sels.Core.Exceptions.Configuration;
using Sels.Core;
using System.IO;
using NLog.Extensions.Logging;
using NLog.Config;
using NLog.Targets;
using Sels.Core.Templates.FileSizes;
using Sels.Core.Components.FileSizes.Byte;
using Sels.Crypto.Chia.PlotBot.Contracts;
using Sels.Crypto.Chia.PlotBot.ValidationProfiles;
using Sels.Core.Components.FileSystem;
using Sels.Core.Components.Factory;
using Sels.Core.Components.Conversion;
using Microsoft.Extensions.Configuration;
using NLog.Common;
using Sels.Core.Components.IoC;
using Sels.Core.Contracts.Conversion;
using Sels.Core.Templates.FileSystem;
using Sels.Crypto.Chia.PlotBot.Components.PlotDelayers;
using Sels.Crypto.Chia.PlotBot.Components.DriveClearers;
using Sels.Core.Extensions.Linq;
using System.Net.Mail;
using Sels.Core.Components.Logging;
using Sels.Crypto.Chia.PlotBot.Components.PlotProgressParsers;
using Sels.Crypto.Chia.PlotBot.Components.Factories;
using Sels.Crypto.Chia.PlotBot.Components.Services;
using Sels.Crypto.Chia.PlotBot.Components.InitializerActions;
using Sels.Core.Cron.Components.ScheduledAction;
using Sels.Core.Components.ScheduledAction;

namespace Sels.Crypto.Chia.PlotBot
{
    public class Program
    {
        // Constants
        private const string ConfigProviderKey = "ConfigProvider";

        public static void Main(string[] args)
        {
            try
            {
                NLog.LogManager.AutoShutdown = false;

                // Set current directory 
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);

                CreateHostBuilder(args).Build().Run();
            }
            finally
            {
                NLog.LogManager.Flush();
                NLog.LogManager.Shutdown();
            }
            
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSystemd()
                .ConfigureServices((hostContext, services) =>
                {
                    // Replace default IConfiguration because publishing breaks the default path
                    services.AddSingleton(x => Helper.App.BuildDefaultConfigurationFile());

                    // Register services
                    services.AddSingleton<IConfigProvider, ConfigProvider>();
                    services.AddSingleton<IPlotBotConfigValidator, ConfigValidationProfile>();
                    if (OperatingSystem.IsLinux()) { services.AddSingleton<IFactory<CrossPlatformDirectory>, LinuxDirectoryFactory>(); } else { services.AddSingleton<IFactory<CrossPlatformDirectory>, WindowsDirectoryFactory>(); }
                    services.AddSingleton<IObjectFactory, AliasTypeFactory>(x => {
                        return new AliasTypeFactory(x.GetRequiredService<IConfigProvider>(), GenericConverter.DefaultConverter);
                    });

                    services.AddSingleton<IGenericTypeConverter, GenericConverter>(x => {
                        var converter = GenericConverter.DefaultConverter;
                        converter.Settings.ThrowOnFailedConversion = false;
                        return converter;
                    });

                    // Create config provider
                    var provider = services.BuildServiceProvider();
                    var configProvider = provider.GetRequiredService<IConfigProvider>();

                    // Check test mode services
                    var testMode = configProvider.GetAppSetting<bool>(PlotBotConstants.Config.AppSettings.TestMode, false);

                    // Load service settings
                    var interval = configProvider.GetAppSetting<int?>(PlotBotConstants.Config.AppSettings.Interval, false, x => x == null || x > 0, x => $"{PlotBotConstants.Config.AppSettings.Interval} must be larger than 0") ?? 60000;
                    var plottingInterval = configProvider.GetAppSetting(PlotBotConstants.Config.AppSettings.PlottingInterval, false) ?? "* * * * *";
                    var retryOnFailed = configProvider.GetAppSetting<bool>(PlotBotConstants.Config.AppSettings.RetryAfterFailed, false);
                    var reduceIdleMessages = configProvider.GetAppSetting<bool>(PlotBotConstants.Config.AppSettings.RetryAfterFailed, false);
                    var validatePlotCommand = configProvider.GetAppSetting<bool?>(PlotBotConstants.Config.AppSettings.ValidatePlotCommand, false) ?? true;
                    var driveClearerIdleTime = configProvider.GetAppSetting<int?>(PlotBotConstants.Config.AppSettings.DriveClearersIdleTime, false, x => x == null || x > 0, x => $"{PlotBotConstants.Config.AppSettings.DriveClearersIdleTime} must be larger than 0") ?? 60;

                    services.AddSingleton(x => {
                        return new PlotBot(x.GetRequiredService<IFactory<CrossPlatformDirectory>>(), x.GetRequiredService<IServiceFactory>(), x.GetRequiredService<IGenericTypeConverter>(), x.GetRequiredService<IPlottingService>(), driveClearerIdleTime, validatePlotCommand, x.GetServices<IPlotBotInitializerAction>());
                    });

                    if (testMode)
                    {
                        services.AddHostedService(x => new TestPlotBotManager(x.GetRequiredService<ILoggerFactory>(), x.GetRequiredService<IConfigProvider>(), x.GetRequiredService<IPlotBotConfigValidator>(), x.GetRequiredService<PlotBot>(), new RecurringCronAction(plottingInterval), new RecurringTimerAction(interval), retryOnFailed, reduceIdleMessages));
                        if (OperatingSystem.IsLinux()) { services.AddSingleton<IPlottingService, TestLinuxPlottingService>(); } else { services.AddSingleton<IPlottingService, WindowsPlottingService>(); }                        
                    }
                    else
                    {
                        services.AddHostedService(x => new PlotBotManager(x.GetRequiredService<ILoggerFactory>(), x.GetRequiredService<IConfigProvider>(), x.GetRequiredService<IPlotBotConfigValidator>(), x.GetRequiredService<PlotBot>(), new RecurringCronAction(plottingInterval), new RecurringTimerAction(interval), retryOnFailed, reduceIdleMessages));
                        if (OperatingSystem.IsLinux()) { services.AddSingleton<IPlottingService, LinuxPlottingService>(); } else { services.AddSingleton<IPlottingService, WindowsPlottingService>(); }                      
                    }

                    // Setup initializer actions
                    if(configProvider.GetAppSetting<bool>(PlotBotConstants.Config.AppSettings.CleanupCache, false))
                    {
                        if (testMode)
                        {
                            services.AddSingleton<IPlotBotInitializerAction, TestCacheCleanerAction>();
                        }
                        else
                        {
                            services.AddSingleton<IPlotBotInitializerAction, CacheCleanerAction>();
                        }
                    }

                    if(configProvider.GetAppSetting<bool>(PlotBotConstants.Config.AppSettings.CleanupFailedCopy, false))
                    {
                        if (testMode)
                        {
                            services.AddSingleton<IPlotBotInitializerAction, TempFileCleanerAction>();
                        }
                        else
                        {
                            services.AddSingleton<IPlotBotInitializerAction, FileCleanerAction>();
                        }
                    }

                    // Setup service factory
                    services.AddSingleton<IServiceFactory, UnityServiceFactory>(x => {
                        var factory = new UnityServiceFactory();
                        factory.LoadFrom(services);

                        // Progress parsers
                        factory.Register<IPlotProgressParser, StringPlotProgressParser>(ServiceScope.Transient, PlotBotConstants.Components.PlotProgressParser.String);
                        factory.Register<IPlotProgressParser, RegexPlotProgressParser>(ServiceScope.Transient, PlotBotConstants.Components.PlotProgressParser.Regex);
                        factory.Register<IPlotProgressParser, MadMaxProgressParser>(ServiceScope.Transient, PlotBotConstants.Components.PlotProgressParser.MadMax);
                        factory.Register<IPlotProgressParser, ChiaProgressParser>(ServiceScope.Transient, PlotBotConstants.Components.PlotProgressParser.Chia);

                        // Plotter delayers
                        factory.Register<IPlotterDelayer, LastStartedDelayer>(ServiceScope.Transient, PlotBotConstants.Components.Delay.TimeStarted);
                        factory.Register<IPlotterDelayer, ProgressFileDelayer>(ServiceScope.Transient, PlotBotConstants.Components.Delay.ProgressFileContains);

                        // Drive clearers
                        if (testMode)
                        {
                            factory.Register<IDriveSpaceClearer, TestOgPlotDateClearer>(ServiceScope.Transient, PlotBotConstants.Components.Clearer.OgDate);
                            factory.Register<IDriveSpaceClearer, TestOgPlotByteClearer>(ServiceScope.Transient, PlotBotConstants.Components.Clearer.OgByte);
                            factory.Register<IDriveSpaceClearer, TestZeroByteClearer>(ServiceScope.Transient, PlotBotConstants.Components.Clearer.ZeroByte);
                        }
                        else
                        {
                            factory.Register<IDriveSpaceClearer, OgPlotDateClearer>(ServiceScope.Transient, PlotBotConstants.Components.Clearer.OgDate);
                            factory.Register<IDriveSpaceClearer, OgPlotByteClearer>(ServiceScope.Transient, PlotBotConstants.Components.Clearer.OgByte);
                            factory.Register<IDriveSpaceClearer, ZeroByteClearer>(ServiceScope.Transient, PlotBotConstants.Components.Clearer.ZeroByte);
                        }

                        return factory;
                    });

                    // Setup logging
                    services.AddLogging(x => SetupLogging(configProvider, x, testMode));
                });

        private static void SetupLogging(IConfigProvider configProvider, ILoggingBuilder builder, bool isTestMode)
        {
            // Read config
            var devMode = configProvider.GetAppSetting<bool>(PlotBotConstants.Config.AppSettings.DevMode, false);
            var minLogLevel = configProvider.GetSectionSetting<LogLevel>(PlotBotConstants.Config.LogSettings.MinLogLevel, nameof(PlotBotConstants.Config.LogSettings));
            var logDirectory = configProvider.GetSectionSetting(PlotBotConstants.Config.LogSettings.LogDirectory, nameof(PlotBotConstants.Config.LogSettings), true, x => x.HasValue() && Directory.Exists(x), x => $"Directory cannot be empty and Directory must exist on the file system. Was <{x}>");
            var archiveSize = configProvider.GetSectionSetting<long>(PlotBotConstants.Config.LogSettings.ArchiveSize, nameof(PlotBotConstants.Config.LogSettings), true, x => x > 1, x => $"File size cannot be empty and file size must be above 1 {MegaByte.FileSizeAbbreviation}");
            var archiveFileSize = FileSize.CreateFromSize<MegaByte>(archiveSize);
            var isDebug = minLogLevel <= LogLevel.Debug;

            // Logging config
            var mailingEnabled = configProvider.IsSectionDefined(nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
            LogLevel minMailLogLevel = LogLevel.Warning;
            string mailSender = string.Empty;
            string mailReceivers = string.Empty;
            string server = string.Empty;
            int port = 1;
            string username = string.Empty;
            string password = string.Empty;
            bool isSsl = false;

            // Read mail config if defined
            if (mailingEnabled)
            {
                minMailLogLevel = configProvider.GetSectionSetting<LogLevel>(PlotBotConstants.Config.LogSettings.Mail.MinLogLevel, true, null, null, nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
                mailSender = configProvider.GetSectionSetting(PlotBotConstants.Config.LogSettings.Mail.Sender, true, HasStringValue, x => CreateConfigValueEmptyMessage(PlotBotConstants.Config.LogSettings.Mail.Sender, x), nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
                mailReceivers = configProvider.GetSectionSetting(PlotBotConstants.Config.LogSettings.Mail.Receivers, true, HasStringValue, null, nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
                server = configProvider.GetSectionSetting(PlotBotConstants.Config.LogSettings.Mail.Server, true, HasStringValue, x => CreateConfigValueEmptyMessage(PlotBotConstants.Config.LogSettings.Mail.Server, x), nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
                port = configProvider.GetSectionSetting<int>(PlotBotConstants.Config.LogSettings.Mail.Port, true, x => x > 0, x => $"Port must be above 0. Was {x}", nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
                username = configProvider.GetSectionSetting(PlotBotConstants.Config.LogSettings.Mail.Username, true, HasStringValue, x => CreateConfigValueEmptyMessage(PlotBotConstants.Config.LogSettings.Mail.Username, x), nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
                password = configProvider.GetSectionSetting(PlotBotConstants.Config.LogSettings.Mail.Password, true, HasStringValue, x => CreateConfigValueEmptyMessage(PlotBotConstants.Config.LogSettings.Mail.Password, x), nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
                isSsl = configProvider.GetSectionSetting<bool>(PlotBotConstants.Config.LogSettings.Mail.Ssl, true, null, null, nameof(PlotBotConstants.Config.LogSettings), nameof(PlotBotConstants.Config.LogSettings.Mail));
            }


            var logDirectoryInfo = new DirectoryInfo(logDirectory);
            var minLogLevelOrdinal = minLogLevel.ConvertTo<int>();

            // Enable nlog internal logging if in dev mode
            if (devMode)
            {
                InternalLogger.LogToConsole = true;
                InternalLogger.LogFile = Path.Combine(logDirectory, "Nlog.txt");
                InternalLogger.LogLevel = NLog.LogLevel.Debug;
            }

            // Clear providers and set basic settings
            builder.ClearProviders();
            builder.SetMinimumLevel(minLogLevel);

            var config = new LoggingConfiguration();

            // Create targets
            if(isDebug) config.AddTarget(CreateLogFileTarget(PlotBotConstants.Logging.Targets.PlotBotDebug, logDirectoryInfo, archiveFileSize));
            config.AddTarget(CreateLogFileTarget(PlotBotConstants.Logging.Targets.PlotBotAll, logDirectoryInfo, archiveFileSize));
            config.AddTarget(CreateLogFileTarget(PlotBotConstants.Logging.Targets.PlotBotError, logDirectoryInfo, archiveFileSize, PlotBotConstants.Logging.FullLayout));
            config.AddTarget(CreateLogFileTarget(PlotBotConstants.Logging.Targets.PlotBotCritical, logDirectoryInfo, archiveFileSize, PlotBotConstants.Logging.FullLayout));

            // Create rules
            var nlogMinLevel = NLog.LogLevel.FromOrdinal(minLogLevelOrdinal);
            // Skip microsoft logs
            config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Info, new NullTarget(), PlotBotConstants.Logging.Categories.Microsoft, true);
            // Debug logs
            if(isDebug) config.AddRule(nlogMinLevel, NLog.LogLevel.Fatal, PlotBotConstants.Logging.Targets.PlotBotDebug);
            // All logs
            config.AddRule(isDebug ? NLog.LogLevel.Info : nlogMinLevel, NLog.LogLevel.Fatal, PlotBotConstants.Logging.Targets.PlotBotAll);
            // All errors
            config.AddRule(minLogLevelOrdinal >= NLog.LogLevel.Warn.Ordinal ? nlogMinLevel : NLog.LogLevel.Warn, NLog.LogLevel.Fatal, PlotBotConstants.Logging.Targets.PlotBotError);
            // Fatal errors only
            config.AddRule(NLog.LogLevel.Fatal, NLog.LogLevel.Fatal, PlotBotConstants.Logging.Targets.PlotBotCritical);

            // Create mail logging
            if (mailingEnabled)
            {
                config.AddTarget(new MailTarget()
                {
                    Name = PlotBotConstants.Logging.Targets.PlotBotMail,
                    SmtpAuthentication = SmtpAuthenticationMode.Basic,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Subject = PlotBotConstants.Logging.MailSubjectLayout,
                    Body = PlotBotConstants.Logging.MailBodyLayout,
                    From = mailSender,
                    To = mailReceivers,
                    Html = false,
                    SmtpServer = server,
                    SmtpPort = port,
                    SmtpUserName = username,
                    SmtpPassword = password,
                    Timeout = 5000,
                    EnableSsl = isSsl
                });

                config.AddRule(NLog.LogLevel.FromOrdinal(minMailLogLevel.ConvertTo<int>()), NLog.LogLevel.Fatal, PlotBotConstants.Logging.Targets.PlotBotMail);
            }

            // Add loggers
            builder.AddConsole();
            builder.AddNLog(config);
        }

        private static bool HasStringValue(string value)
        {
            return value.HasValue();
        }

        private static string CreateConfigValueEmptyMessage(string name, string value)
        {
            return $"{name} cannot be empty or whitespace. Was <{value}>";
        }

        private static FileTarget CreateLogFileTarget(string targetName, DirectoryInfo logDirectory, FileSize archiveSize, string layout = PlotBotConstants.Logging.Layout)
        {
            return new FileTarget()
            {
                Name = targetName,
                Layout = layout,
                FileName = Path.Combine(logDirectory.FullName, $"{targetName}.txt"),
                ArchiveFileName = Path.Combine(logDirectory.FullName, PlotBotConstants.Logging.ArchiveFolder, $"{targetName}_{{###}}.txt"),
                ArchiveAboveSize = archiveSize.ByteSize,
                ArchiveNumbering = ArchiveNumberingMode.DateAndSequence,
                ConcurrentWrites = false
            };
        }
    }
}

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using System;
using System.Globalization;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Text;

namespace UniversalHost.Services
{
    /// <summary>
    /// 日志的功能模块。名称作为 SourceContext 保存，新增模块时在此扩展。
    /// </summary>
    public enum LogModule
    {
        Application,
        Project,
        Symbols,
        Monitoring,
        Xcp,
        Iap,
        Calibration,
        FaultRecord,
        Logging
    }

    public static class LogService
    {
        public static volatile bool IsWriteToFileEnabled;
        // 动态日志等级开关
        private static readonly LoggingLevelSwitch
            _levelSwitch = new(LogEventLevel.Information);
        public static void LogServiceConfig(string logPath, bool isWriteToFileEnabled, CompositeDisposable disposables)
        {
            IsWriteToFileEnabled = isWriteToFileEnabled;
            Log.CloseAndFlush();
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(_levelSwitch)
                .Enrich.FromLogContext()
                // 未显式指定来源的日志也保留可筛选的默认模块。
                .Enrich.WithProperty(Constants.SourceContextPropertyName, nameof(LogModule.Application))
                // 在入队时判断文件开关，避免设置变更影响已经接收的事件。
                .WriteTo.Conditional(_ => IsWriteToFileEnabled, sinks => sinks.Async(file => file.File(
                    formatter: new JsonFormatter(renderMessage: true, formatProvider: CultureInfo.InvariantCulture),
                    path: Path.Combine(logPath, "log_.jsonl"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 50_000_000,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 31,
                    shared: true,
                    encoding: new UTF8Encoding(false),
                    flushToDiskInterval: TimeSpan.FromSeconds(1))))
                .CreateLogger()
                .DisposeWith(disposables);
        }

        /// <summary>
        /// 从当前工程的 Logger 获取模块来源。每次写日志时调用，不缓存返回值，
        /// 避免工程切换后继续引用已释放的 Logger。
        /// </summary>
        public static ILogger ForModule(LogModule module)
        {
            return Log.ForContext(Constants.SourceContextPropertyName, module.ToString());
        }

        /// <summary>
        /// 修改日志等级
        /// </summary>
        public static void SetLogLevel(LogEventLevel level, bool logChange = true)
        {
            if (_levelSwitch.MinimumLevel == level)
            {
                return;
            }

            _levelSwitch.MinimumLevel = level;
            if (logChange)
            {
                ForModule(LogModule.Logging).Information("日志等级修改为 {Level}", level);
            }
        }

        /// <summary>
        /// 获取当前日志等级
        /// </summary>
        public static LogEventLevel GetLogLevel()
        {
            return _levelSwitch.MinimumLevel;
        }
    }
}

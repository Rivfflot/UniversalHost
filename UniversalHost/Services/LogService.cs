using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;

namespace UniversalHost.Services
{
    public static class LogService
    {
        public static bool IsWriteToFileEnabled;
        // 动态日志等级开关
        private static readonly LoggingLevelSwitch
            _levelSwitch = new(LogEventLevel.Information);
        public static void LogServiceConfig(string logPath, bool isWriteToFileEnabled, CompositeDisposable disposables)
        {
            IsWriteToFileEnabled = isWriteToFileEnabled;
            Log.CloseAndFlush();
            var logPathTxt = Path.Combine(logPath, "log_.txt");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(_levelSwitch)
                //.Enrich.FromLogContext()
                .WriteTo.Async(a => a.Conditional(
                    evt => IsWriteToFileEnabled, fileSink => fileSink.File(
                path: logPathTxt,
                // 按天滚动
                rollingInterval: RollingInterval.Day,
                // 单文件大小限制
                fileSizeLimitBytes: 50_000_000,
                // 超过大小自动新文件
                rollOnFileSizeLimit: true,
                // 允许文件共享
                shared: true,
                // 自动刷新
                flushToDiskInterval:
                    TimeSpan.FromSeconds(1),
                outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss.ff} " +
                "[{Level:u3}] " +
                "{Message:lj} " +
                //"{Properties:j}" +
                "{NewLine}{Exception}"
            ))
                )
            .CreateLogger()
            .DisposeWith(disposables);
        }

        /// <summary>
        /// 修改日志等级
        /// </summary>
        public static void SetLogLevel(LogEventLevel level)
        {
            _levelSwitch.MinimumLevel = level;
            Log.Information($"日志等级修改为 {level}");
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

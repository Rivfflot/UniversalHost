using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UniversalHost.Services
{
    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }
    public static class NotificationService
    {
        // 存储每个 Window 实例对应的通知管理器
        private static readonly Dictionary<Window, WindowNotificationManager> _managers = new();

        // 注册窗口的管理器（在 View 的 WhenActivated 中调用）
        public static void Register(Window window, NotificationPosition position = NotificationPosition.BottomRight)
        {
            if (_managers.ContainsKey(window)) return;

            var manager = new WindowNotificationManager(window)
            {
                Position = position,
                MaxItems = 5,
            };
            _managers[window] = manager;

            // 窗口关闭时自动注销，防止内存泄漏
            window.Closed += (s, e) => Unregister(window);
        }

        // 注销窗口
        public static void Unregister(Window window)
        {
            if (_managers.TryGetValue(window, out var manager))
            {
                _managers.Remove(window);
            }
        }
        /// <summary>
        /// 向主窗口显示通知
        /// </summary>
        public static void ShowOnMainWindow(string title, string message, NotificationType type = NotificationType.Info)
        {
            var activeWindow = _managers.Keys.First();

            if (activeWindow != null)
            {
                ShowOnWindow(activeWindow, title, message, type);
            }
        }

        // 核心方法：向当前最上层的活跃窗口发送通知
        public static void Show(string title, string message, NotificationType type = NotificationType.Info)
        {
            // 找到当前处于激活状态(IsActive)的窗口，如果没有，则取最后一个打开的窗口
            var activeWindow = _managers.Keys.FirstOrDefault(w => w.IsActive) ?? _managers.Keys.Last();

            if (activeWindow != null)
            {
                ShowOnWindow(activeWindow, title, message, type);
            }
        }

        // 核心方法：向所有打开的窗口广播通知
        public static void Broadcast(string title, string message, NotificationType type = NotificationType.Info)
        {
            foreach (var window in _managers.Keys.ToList())
            {
                ShowOnWindow(window, title, message, type);
            }
        }

        // 私有辅助方法
        private static void ShowOnWindow(Window window, string title, string message, NotificationType type)
        {
            var avaloniaType = type switch
            {
                Services.NotificationType.Success => Avalonia.Controls.Notifications.NotificationType.Success,
                Services.NotificationType.Warning => Avalonia.Controls.Notifications.NotificationType.Warning,
                Services.NotificationType.Error => Avalonia.Controls.Notifications.NotificationType.Error,
                _ => Avalonia.Controls.Notifications.NotificationType.Information
            };
            if (_managers.TryGetValue(window, out var manager))
            {
                // 确保在 UI 线程执行
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    manager.Show(new Notification(title, message, avaloniaType, TimeSpan.FromSeconds(5)));
                }, Avalonia.Threading.DispatcherPriority.SystemIdle);
            }
        }
    }
}

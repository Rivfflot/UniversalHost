using DynamicData;
using ReactiveUI.Reactive;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading;
using UniversalHost.Models;

namespace UniversalHost.Services;

public static class AppSettingsReactiveObjectExtensions
{
    public static IDisposable SubscribeToAllChanges(
        this ReactiveObject root,
        Action callback)
    {
        // 递归地获取所有嵌套的 ReactiveObject 的 Changed 事件流和 SourceList 变化流
        var allChanges = GetAllChanges(root);

        // 使用 Observable.Merge 合并所有事件流
        // 只忽略订阅时同步发出的集合快照，保留初始化后的第一笔用户修改。
        var initialized = false;
        var subscription = Observable.Merge(allChanges)
            .Where(_ => Volatile.Read(ref initialized))
            //.Do(_ => Debug.WriteLine($"Change detected in {root.GetType().Name}")) // 可选：用于调试哪些对象触发了变更
            .Throttle(TimeSpan.FromSeconds(1)) // 1s 防抖
            .Subscribe(_ =>
            {
                Debug.WriteLine("Auto save triggered."); // 确保这个回调被触发
                callback();
            });
        Volatile.Write(ref initialized, true);
        return subscription;
    }
    private static IEnumerable<IObservable<Unit>> GetAllChanges(object obj, HashSet<object>? visited = null)
    {
        visited ??= new HashSet<object>();

        if (obj == null || obj is Type)
            yield break;

        // 如果已经访问过，直接返回，避免无限递归
        if (!visited.Add(obj))
            yield break;

        if (obj is ReactiveObject reactiveObj)
        {
            yield return reactiveObj.Changed.Select(_ => Unit.Default);
        }

        //添加SourceList
        if (obj is ISourceList<UserSymbolInfo> userSymbols)
            yield return Observable.Merge(
            userSymbols.Connect().Select(_ => Unit.Default),
            userSymbols.Connect().MergeMany(item => item.Changed.Select(_ => Unit.Default))
        );

        if (obj is ISourceList<SymbolInfo> symbols)
            yield return Observable.Merge(
            symbols.Connect().Select(_ => Unit.Default),
            symbols.Connect().MergeMany(item => item.Changed.Select(_ => Unit.Default))
        );

        if (obj is ISourceList<string> paths)
            yield return paths.Connect().Select(_ => Unit.Default);

        //添加SourceCache
        if (obj is ISourceCache<UserSymbolInfo, Guid> userSymbolCache)
        {
            yield return Observable.Merge(
                userSymbolCache.Connect().Select(_ => Unit.Default),
                userSymbolCache.Connect()
                    .MergeMany(item => item.Changed.Select(_ => Unit.Default))
            );
        }

        if (obj is ISourceCache<UserCommand, byte> userCommandCache)
        {
            yield return Observable.Merge(
                userCommandCache.Connect().Select(_ => Unit.Default),
                userCommandCache.Connect()
                    .MergeMany(item => item.Changed.Select(_ => Unit.Default))
            );
        }

        //添加其他项
        var properties = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            if (prop.GetIndexParameters().Length > 0)
                continue;

            if (prop.DeclaringType?.IsGenericParameter == true)
                continue;

            object? value;
            try
            {
                value = prop.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value != null)
            {
                foreach (var observable in GetAllChanges(value, visited))
                {
                    yield return observable;
                }
            }
        }
    }


}

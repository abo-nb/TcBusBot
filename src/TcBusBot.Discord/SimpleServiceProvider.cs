using System.Reflection;
using Microsoft.Extensions.DependencyInjection;   // IServiceScope / IServiceScopeFactory
                                                  // （由 Discord.Net.Interactions 間接帶進來的相依）

namespace TcBusBot.Discord;

/// <summary>
/// 極簡的服務容器。
///
/// 為什麼不用 Microsoft.Extensions.DependencyInjection：
/// 這個環境的 NuGet 快取沒有它，而且本專案只有 5 個單例服務，
/// 寫一個 40 行的容器比拉一整套 DI 更符合「不要過度設計」。
///
/// ⚠️ 但有兩個 Discord.Net 的硬需求必須滿足（踩過）：
///   1. <see cref="IServiceScopeFactory"/> - InteractionService 建構模組時會呼叫
///      <c>services.CreateScope()</c>，缺少它會直接丟
///      「No service for type 'IServiceScopeFactory' has been registered.」
///   2. 模組實例**不能快取** - Discord.Net 會在執行前把 Context 設到模組實例上，
///      若共用同一個實例，兩個並行的互動會互相蓋掉 Context。
///      所以模組型別每次都建新的。
/// </summary>
public sealed class SimpleServiceProvider : IServiceProvider, IServiceScopeFactory
{
    private readonly Dictionary<Type, object> _singletons = new();

    /// <summary>註冊單例。</summary>
    public SimpleServiceProvider Add<T>(T instance) where T : class
    {
        _singletons[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType == typeof(IServiceScopeFactory)) return this;

        if (_singletons.TryGetValue(serviceType, out var instance)) return instance;

        // 沒註冊的型別（例如 Discord.Net 的模組類別）→ 用建構子注入即時建一個。
        // 刻意不快取：模組實例必須每次新建（見類別註解）。
        return TryCreate(serviceType);
    }

    public IServiceScope CreateScope() => new Scope(this);

    private object? TryCreate(Type serviceType)
    {
        if (serviceType.IsAbstract || serviceType.IsInterface) return null;

        var ctor = serviceType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().All(p => Resolvable(p.ParameterType)));

        if (ctor is null) return null;

        var args = ctor.GetParameters().Select(p => GetService(p.ParameterType)).ToArray();
        return ctor.Invoke(args);
    }

    private bool Resolvable(Type type)
        => type == typeof(IServiceProvider)
           || type == typeof(IServiceScopeFactory)
           || _singletons.ContainsKey(type)
           || (!type.IsAbstract && !type.IsInterface && type.GetConstructors().Length > 0);

    /// <summary>沒有 scoped 生命週期，所以 scope 只是回傳自己。</summary>
    private sealed class Scope(IServiceProvider provider) : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = provider;
        public void Dispose() { }
    }
}

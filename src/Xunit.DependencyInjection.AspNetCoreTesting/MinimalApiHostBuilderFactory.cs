﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Xunit.DependencyInjection.AspNetCoreTesting;

public static class MinimalApiHostBuilderFactory
{
    private static readonly Func<IHostBuilder> CreateHostBuilder;
    private static readonly MethodInfo ConfigureHostBuilder;
    private static readonly MethodInfo EntryPointCompleted;
    private static readonly MethodInfo SetHostFactory;
    private static readonly MethodInfo ResolveHostFactory;

    static MinimalApiHostBuilderFactory()
    {
        var deferredHostBuilderType = typeof(WebApplicationFactory<>).Assembly.GetType(
            "Microsoft.AspNetCore.Mvc.Testing.DeferredHostBuilder");

        var hostFactoryResolverType = typeof(WebApplicationFactory<>).Assembly.GetType(
            "Microsoft.Extensions.Hosting.HostFactoryResolver");

        if (deferredHostBuilderType == null || hostFactoryResolverType == null) throw NotSupported();

        CreateHostBuilder = Expression.Lambda<Func<IHostBuilder>>(Expression.New(deferredHostBuilderType)).Compile();

        var configureHostBuilder = deferredHostBuilderType.GetMethod("ConfigureHostBuilder");
        var entryPointCompleted = deferredHostBuilderType.GetMethod("EntryPointCompleted");
        var setHostFactory = deferredHostBuilderType.GetMethod("SetHostFactory");
        var resolveHostFactory = hostFactoryResolverType.GetMethod("ResolveHostFactory");

        if (configureHostBuilder == null || entryPointCompleted == null || setHostFactory == null ||
            resolveHostFactory == null) throw NotSupported();

        ConfigureHostBuilder = configureHostBuilder;
        EntryPointCompleted = entryPointCompleted;
        SetHostFactory = setHostFactory;
        ResolveHostFactory = resolveHostFactory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static NotSupportedException NotSupported() =>
        new("Not support current version of Microsoft.AspNetCore.Mvc.Testing");

    public static IHostBuilder GetHostBuilder<TEntryPoint>(Action<IWebHostBuilder>? configure = null) where TEntryPoint : class
    {
        var entryAssembly = typeof(TEntryPoint).Assembly;
        var deferredHostBuilder = CreateHostBuilder();

        // There's no helper for UseApplicationName, but we need to
        // set the application name to the target entry point
        // assembly name.
        deferredHostBuilder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(
            new[] { KeyValuePair.Create(HostDefaults.ApplicationKey, entryAssembly.GetName().Name) }));

        // This helper call does the hard work to determine if we can fallback to diagnostic source events to get the host instance
        var factory = ResolveHostFactory.Invoke(null, BindingFlags.DoNotWrapExceptions, null, new object?[]
        {
            entryAssembly, (TimeSpan?)null, false,
            ConfigureHostBuilder.CreateDelegate<Action<object>>(deferredHostBuilder),
            EntryPointCompleted.CreateDelegate<Action<Exception>>(deferredHostBuilder)
        }, null);

        ArgumentNullException.ThrowIfNull(factory);

        SetHostFactory.Invoke(deferredHostBuilder, BindingFlags.DoNotWrapExceptions, null, new[] { factory }, null);

        var setContentRoot = typeof(WebApplicationFactory<TEntryPoint>).GetMethod("SetContentRoot",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance) ?? throw NotSupported();

        deferredHostBuilder.ConfigureWebHost(webHostBuilder =>
        {
            setContentRoot.Invoke(setContentRoot.IsStatic ? null : new WebApplicationFactory<TEntryPoint>(),
                BindingFlags.DoNotWrapExceptions, null, new object?[] { webHostBuilder }, null);

            configure?.Invoke(webHostBuilder);

            webHostBuilder.UseTestServer(options => options.PreserveExecutionContext = true);

            webHostBuilder.ConfigureServices(x =>
            {
                x.TryAddSingleton<HttpClient>(sp =>
                    ((TestServer)sp.GetRequiredService<IServer>()).CreateClient());
            });
        });

        return deferredHostBuilder;
    }
}

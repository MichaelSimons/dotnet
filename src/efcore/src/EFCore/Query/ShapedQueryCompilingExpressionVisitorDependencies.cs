// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.Extensions.Caching.Memory;

namespace Microsoft.EntityFrameworkCore.Query;

/// <summary>
///     <para>
///         Service dependencies parameter class for <see cref="ShapedQueryCompilingExpressionVisitor" />
///     </para>
///     <para>
///         This type is typically used by database providers (and other extensions). It is generally
///         not used in application code.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         Do not construct instances of this class directly from either provider or application code as the
///         constructor signature may change as new dependencies are added. Instead, use this type in
///         your constructor so that an instance will be created and injected automatically by the
///         dependency injection container. To create an instance with some dependent services replaced,
///         first resolve the object from the dependency injection container, then replace selected
///         services using the C# 'with' operator. Do not call the constructor at any point in this process.
///     </para>
///     <para>
///         The service lifetime is <see cref="ServiceLifetime.Singleton" /> and multiple registrations
///         are allowed. This means a single instance of each service is used by many <see cref="DbContext" />
///         instances. The implementation must be thread-safe.
///         This service cannot depend on services registered as <see cref="ServiceLifetime.Scoped" />.
///     </para>
/// </remarks>
public sealed record ShapedQueryCompilingExpressionVisitorDependencies
{
    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    /// <remarks>
    ///     Do not call this constructor directly from either provider or application code as it may change
    ///     as new dependencies are added. Instead, use this type in your constructor so that an instance
    ///     will be created and injected automatically by the dependency injection container. To create
    ///     an instance with some dependent services replaced, first resolve the object from the dependency
    ///     injection container, then replace selected services using the C# 'with' operator. Do not call
    ///     the constructor at any point in this process.
    /// </remarks>
    [EntityFrameworkInternal]
    public ShapedQueryCompilingExpressionVisitorDependencies(
        IStructuralTypeMaterializerSource entityMaterializerSource,
        ITypeMappingSource typeMappingSource,
        IMemoryCache memoryCache,
        ICoreSingletonOptions coreSingletonOptions,
        IModel model,
        ILiftableConstantFactory liftableConstantFactory,
        IDiagnosticsLogger<DbLoggerCategory.Query> queryLogger,
        IEnumerable<ISingletonInterceptor> singletonInterceptors,
        IDbContextServices contextServices)
    {
        EntityMaterializerSource = entityMaterializerSource;
        TypeMappingSource = typeMappingSource;
        MemoryCache = memoryCache;
        CoreSingletonOptions = coreSingletonOptions;
        Model = model;
        LiftableConstantFactory = liftableConstantFactory;
        QueryLogger = queryLogger;
        SingletonInterceptors = singletonInterceptors;
        ContextServices = contextServices;
    }

    /// <summary>
    ///     The materializer source.
    /// </summary>
    public IStructuralTypeMaterializerSource EntityMaterializerSource { get; init; }

    /// <summary>
    ///     The type mapping source.
    /// </summary>
    public ITypeMappingSource TypeMappingSource { get; init; }

    /// <summary>
    ///     The memory cache.
    /// </summary>
    public IMemoryCache MemoryCache { get; init; }

    /// <summary>
    ///     Core singleton options.
    /// </summary>
    public ICoreSingletonOptions CoreSingletonOptions { get; init; }

    /// <summary>
    ///     The model.
    /// </summary>
    public IModel Model { get; init; }

    /// <summary>
    ///     The liftable constant factory.
    /// </summary>
    public ILiftableConstantFactory LiftableConstantFactory { get; init; }

    /// <summary>
    ///     The query logger.
    /// </summary>
    public IDiagnosticsLogger<DbLoggerCategory.Query> QueryLogger { get; init; }

    /// <summary>
    ///     Registered singleton interceptors.
    /// </summary>
    public IEnumerable<ISingletonInterceptor> SingletonInterceptors { get; init; }

    /// <summary>
    ///     The context services.
    /// </summary>
    public IDbContextServices ContextServices { get; init; }
}

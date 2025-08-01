// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.Cosmos.Metadata.Internal;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Cosmos-specific extension methods for <see cref="PropertyBuilder" />.
/// </summary>
/// <remarks>
///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
/// </remarks>
public static class CosmosPropertyBuilderExtensions
{
    /// <summary>
    ///     Configures the property name that the property is mapped to when targeting Azure Cosmos.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         If an empty string is supplied, the property will not be persisted.
    ///     </para>
    ///     <para>
    ///         See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///         <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    ///     </para>
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The name of the property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder ToJsonProperty(
        this PropertyBuilder propertyBuilder,
        string name)
    {
        Check.NotNull(name);

        propertyBuilder.Metadata.SetJsonPropertyName(name);

        return propertyBuilder;
    }

    /// <summary>
    ///     Configures the property name that the property is mapped to when targeting Azure Cosmos.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <typeparam name="TProperty">The type of the property being configured.</typeparam>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The name of the property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<TProperty> ToJsonProperty<TProperty>(
        this PropertyBuilder<TProperty> propertyBuilder,
        string name)
        => (PropertyBuilder<TProperty>)ToJsonProperty((PropertyBuilder)propertyBuilder, name);

    /// <summary>
    ///     Configures the property name that the property is mapped to when targeting Azure Cosmos. If an empty string is
    ///     supplied then the property will not be persisted.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The name of the property.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>
    ///     The same builder instance if the configuration was applied,
    ///     <see langword="null" /> otherwise.
    /// </returns>
    public static IConventionPropertyBuilder? ToJsonProperty(
        this IConventionPropertyBuilder propertyBuilder,
        string? name,
        bool fromDataAnnotation = false)
    {
        if (!propertyBuilder.CanSetJsonProperty(name, fromDataAnnotation))
        {
            return null;
        }

        propertyBuilder.Metadata.SetJsonPropertyName(name, fromDataAnnotation);

        return propertyBuilder;
    }

    /// <summary>
    ///     Returns a value indicating whether the given property name can be set.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="name">The name of the property.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the property name can be set.</returns>
    public static bool CanSetJsonProperty(
        this IConventionPropertyBuilder propertyBuilder,
        string? name,
        bool fromDataAnnotation = false)
        => propertyBuilder.CanSetAnnotation(CosmosAnnotationNames.PropertyName, name, fromDataAnnotation);

    /// <summary>
    ///     Configures the property as a vector for Azure Cosmos DB.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="distanceFunction">The distance function for a vector comparisons.</param>
    /// <param name="dimensions">The number of dimensions in the vector.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder IsVectorProperty(
        this PropertyBuilder propertyBuilder,
        DistanceFunction distanceFunction,
        int dimensions)
    {
        propertyBuilder.Metadata.SetVectorDistanceFunction(ValidateVectorDistanceFunction(distanceFunction));
        propertyBuilder.Metadata.SetVectorDimensions(dimensions);

        return propertyBuilder;
    }

    /// <summary>
    ///     Configures the property as a vector for Azure Cosmos DB.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <typeparam name="TProperty">The type of the property being configured.</typeparam>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="distanceFunction">The distance function for a vector comparisons.</param>
    /// <param name="dimensions">The number of dimensions in the vector.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<TProperty> IsVectorProperty<TProperty>(
        this PropertyBuilder<TProperty> propertyBuilder,
        DistanceFunction distanceFunction,
        int dimensions)
        => (PropertyBuilder<TProperty>)IsVectorProperty((PropertyBuilder)propertyBuilder, distanceFunction, dimensions);

    /// <summary>
    ///     Configures the property as a vector for Azure Cosmos DB.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="distanceFunction">The distance function for a vector comparisons.</param>
    /// <param name="dimensions">The number of dimensions in the vector.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>
    ///     The same builder instance if the configuration was applied,
    ///     <see langword="null" /> otherwise.
    /// </returns>
    public static IConventionPropertyBuilder? IsVectorProperty(
        this IConventionPropertyBuilder propertyBuilder,
        DistanceFunction distanceFunction,
        int dimensions,
        bool fromDataAnnotation = false)
    {
        if (!propertyBuilder.CanSetIsVectorProperty(distanceFunction, dimensions, fromDataAnnotation))
        {
            return null;
        }

        propertyBuilder.Metadata.SetVectorDistanceFunction(ValidateVectorDistanceFunction(distanceFunction), fromDataAnnotation);
        propertyBuilder.Metadata.SetVectorDimensions(dimensions, fromDataAnnotation);

        return propertyBuilder;
    }

    /// <summary>
    ///     Returns a value indicating whether the vector distance function and dimensions can be set.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="distanceFunction">The distance function for a vector comparisons.</param>
    /// <param name="dimensions">The number of dimensions in the vector.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the vector distance function and dimensions can be set.</returns>
    public static bool CanSetIsVectorProperty(
        this IConventionPropertyBuilder propertyBuilder,
        DistanceFunction distanceFunction,
        int dimensions,
        bool fromDataAnnotation = false)
        => propertyBuilder.CanSetAnnotation(
            CosmosAnnotationNames.VectorDistanceFunction,
            ValidateVectorDistanceFunction(distanceFunction),
            fromDataAnnotation)
        && propertyBuilder.CanSetAnnotation(
            CosmosAnnotationNames.VectorDimensions,
            dimensions,
            fromDataAnnotation);

    private static DistanceFunction ValidateVectorDistanceFunction(DistanceFunction distanceFunction)
        => Enum.IsDefined(distanceFunction)
            ? distanceFunction
            : throw new ArgumentException(
                CoreStrings.InvalidEnumValue(
                    distanceFunction,
                    nameof(distanceFunction),
                    typeof(DistanceFunction)));

    /// <summary>
    ///     Configures this property to be the etag concurrency token.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder IsETagConcurrency(this PropertyBuilder propertyBuilder)
    {
        propertyBuilder
            .IsConcurrencyToken()
            .ToJsonProperty("_etag")
            .ValueGeneratedOnAddOrUpdate();

        return propertyBuilder;
    }

    /// <summary>
    ///     Configures this property to be the etag concurrency token.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <typeparam name="TProperty">The type of the property being configured.</typeparam>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<TProperty> IsETagConcurrency<TProperty>(
        this PropertyBuilder<TProperty> propertyBuilder)
        => (PropertyBuilder<TProperty>)IsETagConcurrency((PropertyBuilder)propertyBuilder);

    /// <summary>
    ///     Enables full-text search for this property using a specified language.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="language">The language used for full-text search. Setting this to (<see langword="null" /> will use the default language for the container, or "en-US" if default language was not specified.</param>
    /// <param name="enabled">The value indicating whether full-text search should be enabled for this property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder EnableFullTextSearch(
        this PropertyBuilder propertyBuilder,
        string? language = null,
        bool enabled = true)
    {
        propertyBuilder.Metadata.SetIsFullTextSearchEnabled(enabled);
        propertyBuilder.Metadata.SetFullTextSearchLanguage(language);

        return propertyBuilder;
    }

    /// <summary>
    ///     Enables full-text search for this property using a specified language.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <typeparam name="TProperty">The type of the property being configured.</typeparam>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="language">The language used for full-text search. Setting this to (<see langword="null" /> will use the default language for the container, or "en-US" if default language was not specified.</param>
    /// <param name="enabled">The value indicating whether full-text search should be enabled for this property.</param>
    /// <returns>The same builder instance so that multiple calls can be chained.</returns>
    public static PropertyBuilder<TProperty> EnableFullTextSearch<TProperty>(
        this PropertyBuilder<TProperty> propertyBuilder,
        string? language = null,
        bool enabled = true)
        => (PropertyBuilder<TProperty>)EnableFullTextSearch((PropertyBuilder)propertyBuilder, language, enabled);

    /// <summary>
    ///     Enables full-text search for this property using a specified language.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="language">The language used for full-text search. Setting this to (<see langword="null" /> will use the default language for the container, or "en-US" if default language was not specified.</param>
    /// <param name="enabled">The value indicating whether full-text search should be enabled for this property.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns>
    ///     The same builder instance if the configuration was applied,
    ///     <see langword="null" /> otherwise.
    /// </returns>
    public static IConventionPropertyBuilder? EnableFullTextSearch(
        this IConventionPropertyBuilder propertyBuilder,
        string? language,
        bool enabled,
        bool fromDataAnnotation = false)
    {
        if (!propertyBuilder.CanSetEnableFullTextSearch(language, enabled, fromDataAnnotation))
        {
            return null;
        }

        propertyBuilder.Metadata.SetIsFullTextSearchEnabled(enabled, fromDataAnnotation);
        propertyBuilder.Metadata.SetFullTextSearchLanguage(language, fromDataAnnotation);

        return propertyBuilder;
    }

    /// <summary>
    ///     Returns a value indicating whether full-text search can be enabled for this property using a specified language.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-modeling">Modeling entity types and relationships</see>, and
    ///     <see href="https://aka.ms/efcore-docs-cosmos">Accessing Azure Cosmos DB with EF Core</see> for more information and examples.
    /// </remarks>
    /// <param name="propertyBuilder">The builder for the property being configured.</param>
    /// <param name="language">The language for full-text search.</param>
    /// <param name="enabled">The value indicating whether full-text search should be enabled for this property.</param>
    /// <param name="fromDataAnnotation">Indicates whether the configuration was specified using a data annotation.</param>
    /// <returns><see langword="true" /> if the vector type can be set.</returns>
    public static bool CanSetEnableFullTextSearch(
        this IConventionPropertyBuilder propertyBuilder,
        string? language,
        bool enabled,
        bool fromDataAnnotation = false)
        => propertyBuilder.CanSetAnnotation(CosmosAnnotationNames.IsFullTextSearchEnabled, enabled, fromDataAnnotation)
            && propertyBuilder.CanSetAnnotation(CosmosAnnotationNames.FullTextSearchLanguage, language, fromDataAnnotation);
}

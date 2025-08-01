﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Internal;

namespace Microsoft.EntityFrameworkCore.ChangeTracking;

/// <summary>
///     Provides access to change tracking information and operations for a given property of a complex type.
/// </summary>
/// <remarks>
///     <para>
///         Instances of this class are returned from methods when using the <see cref="ChangeTracker" /> API and it is
///         not designed to be directly constructed in your application code.
///     </para>
///     <para>
///         See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
///         examples.
///     </para>
/// </remarks>
/// <typeparam name="TEntity">The type of the entity type that contains the property.</typeparam>
/// <typeparam name="TComplexProperty">The type of the property.</typeparam>
public class ComplexPropertyEntry<TEntity, TComplexProperty> : ComplexPropertyEntry
    where TEntity : class
{
    /// <summary>
    ///     This is an internal API that supports the Entity Framework Core infrastructure and not subject to
    ///     the same compatibility standards as public APIs. It may be changed or removed without notice in
    ///     any release. You should only use it directly in your code with extreme caution and knowing that
    ///     doing so can result in application failures when updating to a new Entity Framework Core release.
    /// </summary>
    [EntityFrameworkInternal]
    public ComplexPropertyEntry(IInternalEntry internalEntry, IComplexProperty complexProperty)
        : base(internalEntry, complexProperty)
    {
    }

    /// <summary>
    ///     The <see cref="EntityEntry{TEntity}" /> to which this member belongs.
    /// </summary>
    /// <value> An entry for the entity that owns this member. </value>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    public new virtual EntityEntry<TEntity> EntityEntry
        => new(InternalEntry.EntityEntry);

    /// <summary>
    ///     Gets or sets the value currently assigned to this property. If the current value is set using this property,
    ///     the change tracker is aware of the change and <see cref="ChangeTracker.DetectChanges" /> is not required
    ///     for the context to detect the change.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    public new virtual TComplexProperty CurrentValue
    {
        get => InternalEntry.GetCurrentValue<TComplexProperty>(Metadata);
        set => base.CurrentValue = value;
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given property of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <param name="propertyExpression">
    ///     A lambda expression representing the property to access information and operations for.
    /// </param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual PropertyEntry<TEntity, TProperty> Property<TProperty>(
        Expression<Func<TComplexProperty, TProperty>> propertyExpression)
    {
        Check.NotNull(propertyExpression);

        return new PropertyEntry<TEntity, TProperty>(
            InternalEntry,
            Metadata.ComplexType.GetProperty(propertyExpression.GetMemberAccess().GetSimpleMemberName()));
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given nested complex type property of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <param name="propertyExpression">
    ///     A lambda expression representing the property to access information and operations for.
    /// </param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual ComplexPropertyEntry<TEntity, TNestedComplexProperty> ComplexProperty<TNestedComplexProperty>(
        Expression<Func<TComplexProperty, TNestedComplexProperty?>> propertyExpression)
    {
        Check.NotNull(propertyExpression);

        return new ComplexPropertyEntry<TEntity, TNestedComplexProperty>(
            InternalEntry,
            Metadata.ComplexType.GetComplexProperty(propertyExpression.GetMemberAccess().GetSimpleMemberName()));
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given complex type property of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="propertyExpression">
    ///     A lambda expression representing the property to access information and operations for.
    /// </param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual ComplexCollectionEntry<TEntity, TElement> ComplexCollection<TElement>(
        Expression<Func<TEntity, IEnumerable<TElement>?>> propertyExpression)
        where TElement : notnull
    {
        Check.NotNull(propertyExpression, nameof(propertyExpression));

        return new ComplexCollectionEntry<TEntity, TElement>(
            InternalEntry,
            Metadata.ComplexType.GetComplexProperty(propertyExpression.GetMemberAccess().GetSimpleMemberName()));
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given property of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <typeparam name="TProperty">The type of the property.</typeparam>
    /// <param name="property">The property to access information and operations for.</param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual PropertyEntry<TEntity, TProperty> Property<TProperty>(IProperty property)
    {
        Check.NotNull(property);

        ValidateType<TProperty>(property);

        return new PropertyEntry<TEntity, TProperty>(InternalEntry, property);
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given nested complex type property of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <typeparam name="TNestedComplexProperty">The type of the property.</typeparam>
    /// <param name="complexProperty">The property to access information and operations for.</param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual ComplexPropertyEntry<TEntity, TNestedComplexProperty> ComplexProperty<TNestedComplexProperty>(
        IComplexProperty complexProperty)
    {
        Check.NotNull(complexProperty);

        ValidateType<TNestedComplexProperty>(complexProperty);

        return new ComplexPropertyEntry<TEntity, TNestedComplexProperty>(InternalEntry, complexProperty);
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given collection property of a complex type on this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="property">The property to access information and operations for.</param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual ComplexCollectionEntry<TEntity, TElement> ComplexCollection<TElement>(IComplexProperty property)
        where TElement : notnull
    {
        Check.NotNull(property, nameof(property));

        ValidateComplexType<TElement>(property);

        return new ComplexCollectionEntry<TEntity, TElement>(InternalEntry, property);
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given property of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <typeparam name="TProperty">The type of the property.</typeparam>
    /// <param name="propertyName">The property to access information and operations for.</param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual PropertyEntry<TEntity, TProperty> Property<TProperty>(string propertyName)
    {
        Check.NotEmpty(propertyName);

        var property = Metadata.ComplexType.GetProperty(propertyName);
        ValidateType<TProperty>(property);

        return new PropertyEntry<TEntity, TProperty>(InternalEntry, property);
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given nested complex type property of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <typeparam name="TNestedComplexProperty">The type of the property.</typeparam>
    /// <param name="propertyName">The property to access information and operations for.</param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual ComplexPropertyEntry<TEntity, TNestedComplexProperty> ComplexProperty<TNestedComplexProperty>(string propertyName)
    {
        Check.NotEmpty(propertyName);

        var property = Metadata.ComplexType.GetComplexProperty(propertyName);
        ValidateType<TNestedComplexProperty>(property);

        return new ComplexPropertyEntry<TEntity, TNestedComplexProperty>(InternalEntry, property);
    }

    /// <summary>
    ///     Provides access to change tracking information and operations for a given collection property of a complex type of this complex type.
    /// </summary>
    /// <remarks>
    ///     See <see href="https://aka.ms/efcore-docs-entity-entries">Accessing tracked entities in EF Core</see> for more information and
    ///     examples.
    /// </remarks>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="propertyName">The property to access information and operations for.</param>
    /// <returns>An object that exposes change tracking information and operations for the given property.</returns>
    public virtual ComplexCollectionEntry<TEntity, TElement> ComplexCollection<TElement>(string propertyName)
        where TElement : notnull
    {
        Check.NotEmpty(propertyName, nameof(propertyName));

        var property = Metadata.ComplexType.GetComplexProperty(propertyName);
        ValidateComplexType<TElement>(property);

        return new ComplexCollectionEntry<TEntity, TElement>(InternalEntry, property);
    }

    private static void ValidateType<TProperty>(IPropertyBase? property)
    {
        if (property != null
            && property.ClrType != typeof(TProperty))
        {
            throw new ArgumentException(
                CoreStrings.WrongGenericPropertyType(
                    property.Name,
                    property.DeclaringType.ClrType.ShortDisplayName(),
                    property.ClrType.ShortDisplayName(),
                    typeof(TProperty).ShortDisplayName()));
        }
    }

    private static void ValidateComplexType<TElement>(IComplexProperty complexProperty)
        where TElement : notnull
    {
        if (complexProperty.ComplexType.ClrType != typeof(TElement))
        {
            throw new ArgumentException(
                CoreStrings.WrongGenericPropertyType(
                    complexProperty.Name,
                    complexProperty.DeclaringType.DisplayName(),
                    complexProperty.ComplexType.ClrType.ShortDisplayName(),
                    typeof(TElement).ShortDisplayName()));
        }
    }
}

﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma warning disable CS0419 // Ambiguous reference in cref attribute

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace Roslyn.Utilities;

/// <summary>
/// A reference-counting wrapper which allows multiple uses of a single disposable object in code, which is
/// deterministically released (by calling <see cref="IDisposable.Dispose"/>) when the last reference is
/// disposed.
/// </summary>
/// <remarks>
/// <para>Each instance of <see cref="ReferenceCountedDisposable{T}"/> represents a counted reference (also
/// referred to as a <em>reference</em> in the following documentation) to a target object. Each of these
/// references has a lifetime, starting when it is constructed and continuing through its release. During
/// this time, the reference is considered <em>alive</em>. Each reference which is alive owns exactly one
/// reference to the target object, ensuring that it will not be disposed while still in use. A reference is
/// released through either of the following actions:</para>
///
/// <list type="bullet">
/// <item>The reference is explicitly released by a call to <see cref="Dispose"/>.</item>
/// <item>The reference is no longer in use by managed code and gets reclaimed by the garbage collector.</item>
/// </list>
///
/// <para>While each instance of <see cref="ReferenceCountedDisposable{T}"/> should be explicitly disposed when
/// the object is no longer needed by the code owning the reference, this implementation will not leak resources
/// in the event one or more callers fail to do so. When all references to an object are explicitly released
/// (i.e. by calling <see cref="Dispose"/>), the target object will itself be deterministically released by a
/// call to <see cref="IDisposable.Dispose"/> when the last reference to it is released. However, in the event
/// one or more references is not explicitly released, the underlying object will still become eligible for
/// non-deterministic release (i.e. finalization) as soon as each reference to it is released by one of the
/// two actions described previously.</para>
///
/// <para>When using <see cref="ReferenceCountedDisposable{T}"/>, certain steps must be taken to ensure the
/// target object is not disposed early.</para>
///
/// <list type="number">
/// <para>Use <see cref="ReferenceCountedDisposable{T}"/> consistently. In other words, do not mix code using
/// reference-counted wrappers with code that references to the target directly.</para>
/// <para>Only use the <see cref="ReferenceCountedDisposable{T}(T)"/> constructor one time per target object.
/// Additional references to the same target object must only be obtained by calling
/// <see cref="TryAddReference"/>.</para>
/// <para>Do not call <see cref="IDisposable.Dispose"/> on the target object directly. It will be called
/// automatically at the appropriate time, as described above.</para>
/// </list>
///
/// <para>All public methods on this type adhere to their pre- and post-conditions and will not invalidate state
/// even in concurrent execution.</para>
/// </remarks>
/// <typeparam name="T">The type of disposable object.</typeparam>
internal sealed class ReferenceCountedDisposable<T> : IReferenceCountedDisposable<T>, IDisposable
    where T : class, IDisposable
{
    /// <summary>
    /// The target of this reference. This value is initialized to a non-<see langword="null"/> value in the
    /// constructor, and set to <see langword="null"/> when the current reference is disposed.
    /// </summary>
    /// <remarks>
    /// <para>This value is only cleared in order to support cases where one or more references is garbage
    /// collected without having <see cref="Dispose"/> called.</para>
    /// </remarks>
    private T? _instance;

    /// <summary>
    /// The boxed reference count, which is shared by all references with the same <see cref="Target"/> object.
    /// </summary>
    /// <remarks>
    /// <para>This field serves as the synchronization object for the current type, since it is shared among all
    /// counted reference to the same target object. Accesses to <see cref="BoxedReferenceCount._referenceCount"/>
    /// should only occur when this object is locked.</para>
    ///
    /// <para>PERF DEV NOTE: A concurrent (but complex) implementation of this type with identical semantics is
    /// available in source control history. The use of exclusive locks was not causing any measurable
    /// performance overhead even on 28-thread machines at the time this was written.</para>
    /// </remarks>
    private readonly BoxedReferenceCount _boxedReferenceCount;

    /// <summary>
    /// Initializes a new reference counting wrapper around an <see cref="IDisposable"/> object.
    /// </summary>
    /// <remarks>
    /// <para>The reference count is initialized to 1.</para>
    /// </remarks>
    /// <param name="instance">The object owned by this wrapper.</param>
    /// <exception cref="ArgumentNullException">
    /// If <paramref name="instance"/> is <see langword="null"/>.
    /// </exception>
    public ReferenceCountedDisposable(T instance)
        : this(instance, new BoxedReferenceCount(1))
    {
    }

    private ReferenceCountedDisposable(T instance, BoxedReferenceCount referenceCount)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));

        // The reference count has already been incremented for this instance
        _boxedReferenceCount = referenceCount;
    }

    /// <summary>
    /// Gets the target object.
    /// </summary>
    /// <remarks>
    /// <para>This call is not valid after <see cref="Dispose"/> is called. If this property or the target
    /// object is used concurrently with a call to <see cref="Dispose"/>, it is possible for the code to be
    /// using a disposed object. After the current instance is disposed, this property throws
    /// <see cref="ObjectDisposedException"/>. However, the exact time when this property starts throwing after
    /// <see cref="Dispose"/> is called is unspecified; code is expected to not use this property or the object
    /// it returns after any code invokes <see cref="Dispose"/>.</para>
    /// </remarks>
    /// <value>The target object.</value>
    public T Target => _instance ?? throw new ObjectDisposedException(nameof(ReferenceCountedDisposable<>));

    /// <summary>
    /// Increments the reference count for the disposable object, and returns a new disposable reference to it.
    /// </summary>
    /// <remarks>
    /// <para>The returned object is an independent reference to the same underlying object. Disposing of the
    /// returned value multiple times will only cause the reference count to be decreased once.</para>
    /// </remarks>
    /// <returns>A new <see cref="ReferenceCountedDisposable{T}"/> pointing to the same underlying object, if it
    /// has not yet been disposed; otherwise, <see langword="null"/> if this reference to the underlying object
    /// has already been disposed.</returns>
    public ReferenceCountedDisposable<T>? TryAddReference()
        => TryAddReferenceImpl(_instance, _boxedReferenceCount);

    IReferenceCountedDisposable<T>? IReferenceCountedDisposable<T>.TryAddReference()
        => TryAddReference();

    /// <summary>
    /// Provides the implementation for <see cref="TryAddReference"/> and
    /// <see cref="WeakReference.TryAddReference"/>.
    /// </summary>
    private static ReferenceCountedDisposable<T>? TryAddReferenceImpl(T? target, BoxedReferenceCount referenceCount)
    {
        lock (referenceCount)
        {
            if (referenceCount._referenceCount == 0)
            {
                // The target is already disposed, and cannot be reused
                return null;
            }

            if (target == null)
            {
                // The current reference has been disposed, so even though it isn't disposed yet we don't have a
                // reference to the target
                return null;
            }

            checked
            {
                referenceCount._referenceCount++;
            }

            // Must return a new instance, in order for the Dispose operation on each individual instance to
            // be idempotent.
            return new ReferenceCountedDisposable<T>(target, referenceCount);
        }
    }

    /// <summary>
    /// Releases the current reference, causing the underlying object to be disposed if this was the last
    /// reference.
    /// </summary>
    /// <remarks>
    /// <para>After this instance is disposed, the <see cref="TryAddReference"/> method can no longer be used to
    /// obtain a new reference to the target, even if other references to the target object are still in
    /// use.</para>
    /// </remarks>
    public void Dispose()
    {
        var instanceToDispose = DisposeImpl();
        instanceToDispose?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        var instanceToDispose = DisposeImpl();
        if (instanceToDispose == null)
            return ValueTask.CompletedTask;

        if (instanceToDispose is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        instanceToDispose.Dispose();
        return ValueTask.CompletedTask;
    }

    private T? DisposeImpl()
    {
        T? instanceToDispose = null;
        lock (_boxedReferenceCount)
        {
            if (_instance == null)
            {
                // Already disposed; allow multiple without error.
                return null;
            }

            _boxedReferenceCount._referenceCount--;
            if (_boxedReferenceCount._referenceCount == 0)
            {
                instanceToDispose = _instance;
            }

            // Ensure multiple calls to Dispose for this instance are a NOP.
            _instance = null;
        }

        return instanceToDispose;
    }

    /// <summary>
    /// Represents a weak reference to a <see cref="ReferenceCountedDisposable{T}"/> which is capable of
    /// obtaining a new counted reference up until the point when the object is no longer accessible.
    /// </summary>
    /// <remarks>
    /// This value type holds a single field, which is not subject to torn reads/writes.
    /// </remarks>
    public readonly struct WeakReference
    {
        private readonly BoxedReferenceCount? _boxedReferenceCount;

        public WeakReference(ReferenceCountedDisposable<T> reference)
            : this()
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            var instance = reference._instance;
            var referenceCount = reference._boxedReferenceCount;
            if (instance == null)
            {
                // The specified reference is already not valid. This case is supported by WeakReference (not
                // unlike `new System.WeakReference(null)`), but we return early to avoid an unnecessary
                // allocation in this case.
                //
                // ⚠ Note that in cases where referenceCount._weakInstance is already non-null, it would be
                // possible to reconstruct a strong reference even though the current reference instance was
                // disposed. This case is intentionally not supported (by checking 'instance' before checking
                // 'referenceCount._weakInstance'), since it would be confusing semantics if this constructor
                // sometimes worked from a disposed reference and sometimes did not.
                return;
            }

            // We only need to allocate a new WeakReference<T> for this reference if one has not already been
            // created for it.
            InterlockedOperations.Initialize(ref referenceCount._weakInstance, static instance => new WeakReference<T>(instance), instance);

            _boxedReferenceCount = referenceCount;
        }

        /// <summary>
        /// Increments the reference count for the disposable object, and returns a new disposable reference to
        /// it.
        /// </summary>
        /// <remarks>
        /// <para>Unlike <see cref="ReferenceCountedDisposable{T}.TryAddReference"/>, this method is capable of
        /// adding a reference to the underlying instance all the way up to the point where it is finally
        /// disposed.</para>
        ///
        /// <para>The returned object is an independent reference to the same underlying object. Disposing of
        /// the returned value multiple times will only cause the reference count to be decreased once.</para>
        /// </remarks>
        /// <returns>A new <see cref="ReferenceCountedDisposable{T}"/> pointing to the same underlying object,
        /// if it has not yet been disposed; otherwise, <see langword="null"/> if the underlying object has
        /// already been disposed.</returns>
        public ReferenceCountedDisposable<T>? TryAddReference()
        {
            // Ensure 'this' is only read once by this method.
            var referenceCount = _boxedReferenceCount;
            if (referenceCount == null)
            {
                // This is either a default(WeakReference), or the current instance was constructed from a reference
                // that was already disposed. No target is available.
                return null;
            }

            var weakInstance = referenceCount._weakInstance;

            // _weakInstance is initialized by the constructor before assigning a value to _boxedReferenceCount.
            // Since it latches in a non-null state, it cannot be null at this point.
            Contract.ThrowIfNull(weakInstance);

            if (!weakInstance.TryGetTarget(out var target))
            {
                // The weak reference has already been collected, so the target is no longer available.
                return null;
            }

            return TryAddReferenceImpl(target, referenceCount);
        }
    }

    /// <summary>
    /// Holds the reference count associated with a disposable object.
    /// </summary>
    private sealed class BoxedReferenceCount(int referenceCount)
    {
        /// <summary>
        /// Holds the weak reference used by instances of <see cref="WeakReference"/> to obtain a reference-counted
        /// reference to the original object. This field is initialized the first time a weak reference is obtained
        /// for the instance, and latches in a non-null state once initialized.
        /// </summary>
        /// <remarks>
        /// DO NOT DISPOSE OF THE TARGET.
        /// </remarks>
        [DisallowNull]
        public WeakReference<T>? _weakInstance;

        public int _referenceCount = referenceCount;
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
// ------------------------------------------------------------------------------
// Changes to this file must follow the http://aka.ms/api-review process.
// ------------------------------------------------------------------------------
[assembly: System.Runtime.CompilerServices.CompilationRelaxations(8)]
[assembly: System.Runtime.CompilerServices.RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: System.Diagnostics.Debuggable(System.Diagnostics.DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: System.Security.AllowPartiallyTrustedCallers]
[assembly: System.Runtime.CompilerServices.ReferenceAssembly]
[assembly: System.Reflection.AssemblyTitle("System.Threading")]
[assembly: System.Reflection.AssemblyDescription("System.Threading")]
[assembly: System.Reflection.AssemblyDefaultAlias("System.Threading")]
[assembly: System.Reflection.AssemblyCompany("Microsoft Corporation")]
[assembly: System.Reflection.AssemblyProduct("Microsoft® .NET Framework")]
[assembly: System.Reflection.AssemblyCopyright("© Microsoft Corporation.  All rights reserved.")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.24212.01")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.24212.01. Commit Hash: 9688ddbb62c04189cac4c4a06e31e93377dccd41")]
[assembly: System.CLSCompliant(true)]
[assembly: System.Reflection.AssemblyMetadata(".NETFrameworkAssembly", "")]
[assembly: System.Reflection.AssemblyMetadata("Serviceable", "True")]
[assembly: System.Reflection.AssemblyVersionAttribute("4.0.10.0")]
[assembly: System.Reflection.AssemblyFlagsAttribute((System.Reflection.AssemblyNameFlags)0x70)]
namespace System.Threading
{
    public partial class AbandonedMutexException : Exception
    {
        public AbandonedMutexException() { }

        public AbandonedMutexException(int location, WaitHandle handle) { }

        public AbandonedMutexException(string message, Exception inner, int location, WaitHandle handle) { }

        public AbandonedMutexException(string message, Exception inner) { }

        public AbandonedMutexException(string message, int location, WaitHandle handle) { }

        public AbandonedMutexException(string message) { }

        public Mutex Mutex { get { throw null; } }

        public int MutexIndex { get { throw null; } }
    }

    public partial struct AsyncLocalValueChangedArgs<T>
    {
        public T CurrentValue { get { throw null; } }

        public T PreviousValue { get { throw null; } }

        public bool ThreadContextChanged { get { throw null; } }
    }

    public sealed partial class AsyncLocal<T>
    {
        public AsyncLocal() { }

        public AsyncLocal(Action<AsyncLocalValueChangedArgs<T>> valueChangedHandler) { }

        public T Value { get { throw null; } set { } }
    }

    public sealed partial class AutoResetEvent : EventWaitHandle
    {
        public AutoResetEvent(bool initialState) : base(default, default) { }
    }

    public partial class Barrier : IDisposable
    {
        public Barrier(int participantCount, Action<Barrier> postPhaseAction) { }

        public Barrier(int participantCount) { }

        public long CurrentPhaseNumber { get { throw null; } }

        public int ParticipantCount { get { throw null; } }

        public int ParticipantsRemaining { get { throw null; } }

        public long AddParticipant() { throw null; }

        public long AddParticipants(int participantCount) { throw null; }

        public void Dispose() { }

        protected virtual void Dispose(bool disposing) { }

        public void RemoveParticipant() { }

        public void RemoveParticipants(int participantCount) { }

        public void SignalAndWait() { }

        public bool SignalAndWait(int millisecondsTimeout, CancellationToken cancellationToken) { throw null; }

        public bool SignalAndWait(int millisecondsTimeout) { throw null; }

        public void SignalAndWait(CancellationToken cancellationToken) { }

        public bool SignalAndWait(TimeSpan timeout, CancellationToken cancellationToken) { throw null; }

        public bool SignalAndWait(TimeSpan timeout) { throw null; }
    }

    public partial class BarrierPostPhaseException : Exception
    {
        public BarrierPostPhaseException() { }

        public BarrierPostPhaseException(Exception innerException) { }

        public BarrierPostPhaseException(string message, Exception innerException) { }

        public BarrierPostPhaseException(string message) { }
    }

    public delegate void ContextCallback(object state);
    public partial class CountdownEvent : IDisposable
    {
        public CountdownEvent(int initialCount) { }

        public int CurrentCount { get { throw null; } }

        public int InitialCount { get { throw null; } }

        public bool IsSet { get { throw null; } }

        public WaitHandle WaitHandle { get { throw null; } }

        public void AddCount() { }

        public void AddCount(int signalCount) { }

        public void Dispose() { }

        protected virtual void Dispose(bool disposing) { }

        public void Reset() { }

        public void Reset(int count) { }

        public bool Signal() { throw null; }

        public bool Signal(int signalCount) { throw null; }

        public bool TryAddCount() { throw null; }

        public bool TryAddCount(int signalCount) { throw null; }

        public void Wait() { }

        public bool Wait(int millisecondsTimeout, CancellationToken cancellationToken) { throw null; }

        public bool Wait(int millisecondsTimeout) { throw null; }

        public void Wait(CancellationToken cancellationToken) { }

        public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) { throw null; }

        public bool Wait(TimeSpan timeout) { throw null; }
    }

    public enum EventResetMode
    {
        AutoReset = 0,
        ManualReset = 1
    }

    public partial class EventWaitHandle : WaitHandle
    {
        public EventWaitHandle(bool initialState, EventResetMode mode, string name, out bool createdNew) { throw null; }

        public EventWaitHandle(bool initialState, EventResetMode mode, string name) { }

        public EventWaitHandle(bool initialState, EventResetMode mode) { }

        public static EventWaitHandle OpenExisting(string name) { throw null; }

        public bool Reset() { throw null; }

        public bool Set() { throw null; }

        public static bool TryOpenExisting(string name, out EventWaitHandle result) { throw null; }
    }

    public sealed partial class ExecutionContext
    {
        internal ExecutionContext() { }

        public static ExecutionContext Capture() { throw null; }

        public static void Run(ExecutionContext executionContext, ContextCallback callback, object state) { }
    }

    public static partial class Interlocked
    {
        public static int Add(ref int location1, int value) { throw null; }

        public static long Add(ref long location1, long value) { throw null; }

        public static double CompareExchange(ref double location1, double value, double comparand) { throw null; }

        public static int CompareExchange(ref int location1, int value, int comparand) { throw null; }

        public static long CompareExchange(ref long location1, long value, long comparand) { throw null; }

        public static IntPtr CompareExchange(ref IntPtr location1, IntPtr value, IntPtr comparand) { throw null; }

        public static object CompareExchange(ref object location1, object value, object comparand) { throw null; }

        public static float CompareExchange(ref float location1, float value, float comparand) { throw null; }

        public static T CompareExchange<T>(ref T location1, T value, T comparand)
            where T : class { throw null; }

        public static int Decrement(ref int location) { throw null; }

        public static long Decrement(ref long location) { throw null; }

        public static double Exchange(ref double location1, double value) { throw null; }

        public static int Exchange(ref int location1, int value) { throw null; }

        public static long Exchange(ref long location1, long value) { throw null; }

        public static IntPtr Exchange(ref IntPtr location1, IntPtr value) { throw null; }

        public static object Exchange(ref object location1, object value) { throw null; }

        public static float Exchange(ref float location1, float value) { throw null; }

        public static T Exchange<T>(ref T location1, T value)
            where T : class { throw null; }

        public static int Increment(ref int location) { throw null; }

        public static long Increment(ref long location) { throw null; }

        public static void MemoryBarrier() { }

        public static long Read(ref long location) { throw null; }
    }

    public static partial class LazyInitializer
    {
        public static T EnsureInitialized<T>(ref T target, ref bool initialized, ref object syncLock, Func<T> valueFactory) { throw null; }

        public static T EnsureInitialized<T>(ref T target, ref bool initialized, ref object syncLock) { throw null; }

        public static T EnsureInitialized<T>(ref T target, Func<T> valueFactory)
            where T : class { throw null; }

        public static T EnsureInitialized<T>(ref T target)
            where T : class { throw null; }
    }

    public partial class LockRecursionException : Exception
    {
        public LockRecursionException() { }

        public LockRecursionException(string message, Exception innerException) { }

        public LockRecursionException(string message) { }
    }

    public enum LockRecursionPolicy
    {
        NoRecursion = 0,
        SupportsRecursion = 1
    }

    public sealed partial class ManualResetEvent : EventWaitHandle
    {
        public ManualResetEvent(bool initialState) : base(default, default) { }
    }

    public partial class ManualResetEventSlim : IDisposable
    {
        public ManualResetEventSlim() { }

        public ManualResetEventSlim(bool initialState, int spinCount) { }

        public ManualResetEventSlim(bool initialState) { }

        public bool IsSet { get { throw null; } }

        public int SpinCount { get { throw null; } }

        public WaitHandle WaitHandle { get { throw null; } }

        public void Dispose() { }

        protected virtual void Dispose(bool disposing) { }

        public void Reset() { }

        public void Set() { }

        public void Wait() { }

        public bool Wait(int millisecondsTimeout, CancellationToken cancellationToken) { throw null; }

        public bool Wait(int millisecondsTimeout) { throw null; }

        public void Wait(CancellationToken cancellationToken) { }

        public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) { throw null; }

        public bool Wait(TimeSpan timeout) { throw null; }
    }

    public static partial class Monitor
    {
        public static void Enter(object obj, ref bool lockTaken) { }

        public static void Enter(object obj) { }

        public static void Exit(object obj) { }

        public static bool IsEntered(object obj) { throw null; }

        public static void Pulse(object obj) { }

        public static void PulseAll(object obj) { }

        public static void TryEnter(object obj, ref bool lockTaken) { }

        public static void TryEnter(object obj, int millisecondsTimeout, ref bool lockTaken) { }

        public static bool TryEnter(object obj, int millisecondsTimeout) { throw null; }

        public static void TryEnter(object obj, TimeSpan timeout, ref bool lockTaken) { }

        public static bool TryEnter(object obj, TimeSpan timeout) { throw null; }

        public static bool TryEnter(object obj) { throw null; }

        public static bool Wait(object obj, int millisecondsTimeout) { throw null; }

        public static bool Wait(object obj, TimeSpan timeout) { throw null; }

        public static bool Wait(object obj) { throw null; }
    }

    public sealed partial class Mutex : WaitHandle
    {
        public Mutex() { }

        public Mutex(bool initiallyOwned, string name, out bool createdNew) { throw null; }

        public Mutex(bool initiallyOwned, string name) { }

        public Mutex(bool initiallyOwned) { }

        public static Mutex OpenExisting(string name) { throw null; }

        public void ReleaseMutex() { }

        public static bool TryOpenExisting(string name, out Mutex result) { throw null; }
    }

    public partial class ReaderWriterLockSlim : IDisposable
    {
        public ReaderWriterLockSlim() { }

        public ReaderWriterLockSlim(LockRecursionPolicy recursionPolicy) { }

        public int CurrentReadCount { get { throw null; } }

        public bool IsReadLockHeld { get { throw null; } }

        public bool IsUpgradeableReadLockHeld { get { throw null; } }

        public bool IsWriteLockHeld { get { throw null; } }

        public LockRecursionPolicy RecursionPolicy { get { throw null; } }

        public int RecursiveReadCount { get { throw null; } }

        public int RecursiveUpgradeCount { get { throw null; } }

        public int RecursiveWriteCount { get { throw null; } }

        public int WaitingReadCount { get { throw null; } }

        public int WaitingUpgradeCount { get { throw null; } }

        public int WaitingWriteCount { get { throw null; } }

        public void Dispose() { }

        public void EnterReadLock() { }

        public void EnterUpgradeableReadLock() { }

        public void EnterWriteLock() { }

        public void ExitReadLock() { }

        public void ExitUpgradeableReadLock() { }

        public void ExitWriteLock() { }

        public bool TryEnterReadLock(int millisecondsTimeout) { throw null; }

        public bool TryEnterReadLock(TimeSpan timeout) { throw null; }

        public bool TryEnterUpgradeableReadLock(int millisecondsTimeout) { throw null; }

        public bool TryEnterUpgradeableReadLock(TimeSpan timeout) { throw null; }

        public bool TryEnterWriteLock(int millisecondsTimeout) { throw null; }

        public bool TryEnterWriteLock(TimeSpan timeout) { throw null; }
    }

    public sealed partial class Semaphore : WaitHandle
    {
        public Semaphore(int initialCount, int maximumCount, string name, out bool createdNew) { throw null; }

        public Semaphore(int initialCount, int maximumCount, string name) { }

        public Semaphore(int initialCount, int maximumCount) { }

        public static Semaphore OpenExisting(string name) { throw null; }

        public int Release() { throw null; }

        public int Release(int releaseCount) { throw null; }

        public static bool TryOpenExisting(string name, out Semaphore result) { throw null; }
    }

    public partial class SemaphoreFullException : Exception
    {
        public SemaphoreFullException() { }

        public SemaphoreFullException(string message, Exception innerException) { }

        public SemaphoreFullException(string message) { }
    }

    public partial class SemaphoreSlim : IDisposable
    {
        public SemaphoreSlim(int initialCount, int maxCount) { }

        public SemaphoreSlim(int initialCount) { }

        public WaitHandle AvailableWaitHandle { get { throw null; } }

        public int CurrentCount { get { throw null; } }

        public void Dispose() { }

        protected virtual void Dispose(bool disposing) { }

        public int Release() { throw null; }

        public int Release(int releaseCount) { throw null; }

        public void Wait() { }

        public bool Wait(int millisecondsTimeout, CancellationToken cancellationToken) { throw null; }

        public bool Wait(int millisecondsTimeout) { throw null; }

        public void Wait(CancellationToken cancellationToken) { }

        public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) { throw null; }

        public bool Wait(TimeSpan timeout) { throw null; }

        public Tasks.Task WaitAsync() { throw null; }

        public Tasks.Task<bool> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken) { throw null; }

        public Tasks.Task<bool> WaitAsync(int millisecondsTimeout) { throw null; }

        public Tasks.Task WaitAsync(CancellationToken cancellationToken) { throw null; }

        public Tasks.Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) { throw null; }

        public Tasks.Task<bool> WaitAsync(TimeSpan timeout) { throw null; }
    }

    public delegate void SendOrPostCallback(object state);
    public partial struct SpinLock
    {
        public SpinLock(bool enableThreadOwnerTracking) { }

        public bool IsHeld { get { throw null; } }

        public bool IsHeldByCurrentThread { get { throw null; } }

        public bool IsThreadOwnerTrackingEnabled { get { throw null; } }

        public void Enter(ref bool lockTaken) { }

        public void Exit() { }

        public void Exit(bool useMemoryBarrier) { }

        public void TryEnter(ref bool lockTaken) { }

        public void TryEnter(int millisecondsTimeout, ref bool lockTaken) { }

        public void TryEnter(TimeSpan timeout, ref bool lockTaken) { }
    }

    public partial struct SpinWait
    {
        public int Count { get { throw null; } }

        public bool NextSpinWillYield { get { throw null; } }

        public void Reset() { }

        public void SpinOnce() { }

        public static bool SpinUntil(Func<bool> condition, int millisecondsTimeout) { throw null; }

        public static bool SpinUntil(Func<bool> condition, TimeSpan timeout) { throw null; }

        public static void SpinUntil(Func<bool> condition) { }
    }

    public partial class SynchronizationContext
    {
        public static SynchronizationContext Current { get { throw null; } }

        public virtual SynchronizationContext CreateCopy() { throw null; }

        public virtual void OperationCompleted() { }

        public virtual void OperationStarted() { }

        public virtual void Post(SendOrPostCallback d, object state) { }

        public virtual void Send(SendOrPostCallback d, object state) { }

        public static void SetSynchronizationContext(SynchronizationContext syncContext) { }
    }

    public partial class SynchronizationLockException : Exception
    {
        public SynchronizationLockException() { }

        public SynchronizationLockException(string message, Exception innerException) { }

        public SynchronizationLockException(string message) { }
    }

    public partial class ThreadLocal<T> : IDisposable
    {
        public ThreadLocal() { }

        public ThreadLocal(bool trackAllValues) { }

        public ThreadLocal(Func<T> valueFactory, bool trackAllValues) { }

        public ThreadLocal(Func<T> valueFactory) { }

        public bool IsValueCreated { get { throw null; } }

        public T Value { get { throw null; } set { } }

        public Collections.Generic.IList<T> Values { get { throw null; } }

        public void Dispose() { }

        protected virtual void Dispose(bool disposing) { }

        ~ThreadLocal() {
        }

        public override string ToString() { throw null; }
    }

    public static partial class Volatile
    {
        public static bool Read(ref bool location) { throw null; }

        public static byte Read(ref byte location) { throw null; }

        public static double Read(ref double location) { throw null; }

        public static short Read(ref short location) { throw null; }

        public static int Read(ref int location) { throw null; }

        public static long Read(ref long location) { throw null; }

        public static IntPtr Read(ref IntPtr location) { throw null; }

        [CLSCompliant(false)]
        public static sbyte Read(ref sbyte location) { throw null; }

        public static float Read(ref float location) { throw null; }

        [CLSCompliant(false)]
        public static ushort Read(ref ushort location) { throw null; }

        [CLSCompliant(false)]
        public static uint Read(ref uint location) { throw null; }

        [CLSCompliant(false)]
        public static ulong Read(ref ulong location) { throw null; }

        [CLSCompliant(false)]
        public static UIntPtr Read(ref UIntPtr location) { throw null; }

        public static T Read<T>(ref T location)
            where T : class { throw null; }

        public static void Write(ref bool location, bool value) { }

        public static void Write(ref byte location, byte value) { }

        public static void Write(ref double location, double value) { }

        public static void Write(ref short location, short value) { }

        public static void Write(ref int location, int value) { }

        public static void Write(ref long location, long value) { }

        public static void Write(ref IntPtr location, IntPtr value) { }

        [CLSCompliant(false)]
        public static void Write(ref sbyte location, sbyte value) { }

        public static void Write(ref float location, float value) { }

        [CLSCompliant(false)]
        public static void Write(ref ushort location, ushort value) { }

        [CLSCompliant(false)]
        public static void Write(ref uint location, uint value) { }

        [CLSCompliant(false)]
        public static void Write(ref ulong location, ulong value) { }

        [CLSCompliant(false)]
        public static void Write(ref UIntPtr location, UIntPtr value) { }

        public static void Write<T>(ref T location, T value)
            where T : class { }
    }

    public partial class WaitHandleCannotBeOpenedException : Exception
    {
        public WaitHandleCannotBeOpenedException() { }

        public WaitHandleCannotBeOpenedException(string message, Exception innerException) { }

        public WaitHandleCannotBeOpenedException(string message) { }
    }
}
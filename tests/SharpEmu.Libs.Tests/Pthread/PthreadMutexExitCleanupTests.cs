// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// A guest thread that terminates while owning a mutex — or while parked waiting
// for one — strands that mutex: ownership is otherwise cleared only by an
// explicit unlock, and a waiter dequeues itself only on the unwind path a killed
// thread never takes. Under FIFO hand-off a dead entry is worse than stale, it
// receives ownership no live thread can release. AbandonMutexesForThread is the
// teardown sweep that fixes this.
//
// These drive the sweep directly with synthetic handles. That is deliberate: the
// waiter queue only ever accepts the calling thread's own handle, so a genuinely
// dead entry cannot be produced by real threads in-process, and a test built on
// live threads cannot reproduce the wedge at all.
public sealed class PthreadMutexExitCleanupTests
{
    private const ulong DeadThread = 0xAAAA_0000_0000_0001;
    private const ulong SurvivorThread = 0xBBBB_0000_0000_0002;

    [Fact]
    public void ExitingWaiterIsDequeuedAndSuccessorIsGranted()
    {
        var (address, state) = RegisterMutex();
        try
        {
            // Dead thread is queued at the head; a survivor is queued behind it.
            EnqueueWaiter(state, DeadThread);
            EnqueueWaiter(state, SurvivorThread);
            Assert.Equal(0UL, OwnerThreadId(state));
            Assert.Equal([DeadThread, SurvivorThread], QueueContents(state));

            _ = KernelPthreadCompatExports.AbandonMutexesForThread(DeadThread, "test_exit");

            // The ghost waiter is gone and the survivor was handed the mutex,
            // instead of being wedged behind a head that never wakes.
            Assert.Equal(SurvivorThread, OwnerThreadId(state));
            Assert.Equal(1, RecursionCount(state));
            Assert.Equal(0, WaiterQueueLength(state));
        }
        finally
        {
            UnregisterMutex(address);
        }
    }

    [Fact]
    public void ExitingOwnerReleasesLockAndHandsItToWaiter()
    {
        var (address, state) = RegisterMutex();
        try
        {
            // Dead thread owns the mutex; a survivor is parked waiting for it.
            SetOwner(state, DeadThread, recursion: 1);
            EnqueueWaiter(state, SurvivorThread);

            var released = KernelPthreadCompatExports.AbandonMutexesForThread(DeadThread, "test_exit");

            Assert.Equal(1, released);
            Assert.Equal(SurvivorThread, OwnerThreadId(state));
            Assert.Equal(1, RecursionCount(state));
            Assert.Equal(0, WaiterQueueLength(state));
        }
        finally
        {
            UnregisterMutex(address);
        }
    }

    [Fact]
    public void ExitingUncontendedOwnerLeavesMutexFree()
    {
        var (address, state) = RegisterMutex();
        try
        {
            SetOwner(state, DeadThread, recursion: 2);

            var released = KernelPthreadCompatExports.AbandonMutexesForThread(DeadThread, "test_exit");

            // No waiters: the mutex is simply released, available to the next locker.
            Assert.Equal(1, released);
            Assert.Equal(0UL, OwnerThreadId(state));
            Assert.Equal(0, RecursionCount(state));
            Assert.Equal(0, WaiterQueueLength(state));
        }
        finally
        {
            UnregisterMutex(address);
        }
    }

    [Fact]
    public void SweepIgnoresMutexesTheThreadNeverTouched()
    {
        var (address, state) = RegisterMutex();
        try
        {
            SetOwner(state, SurvivorThread, recursion: 1);
            EnqueueWaiter(state, SurvivorThread);

            var released = KernelPthreadCompatExports.AbandonMutexesForThread(DeadThread, "test_exit");

            Assert.Equal(0, released);
            Assert.Equal(SurvivorThread, OwnerThreadId(state));
            Assert.Equal(1, WaiterQueueLength(state));
        }
        finally
        {
            UnregisterMutex(address);
        }
    }

    // --- reflection helpers over the private synchronization internals ---

    private static readonly Type ExportsType = typeof(KernelPthreadCompatExports);

    private static readonly Type MutexStateType =
        ExportsType.GetNestedType("PthreadMutexState", BindingFlags.NonPublic)!;

    private static IDictionary MutexStates =>
        (IDictionary)ExportsType
            .GetField("_mutexStates", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static ulong _nextAddress = 0xDEAD_0000_0000_0000;

    private static (ulong Address, object State) RegisterMutex()
    {
        var state = Activator.CreateInstance(MutexStateType, nonPublic: true)!;
        var address = Interlocked.Increment(ref _nextAddress);
        MutexStates[address] = state;
        return (address, state);
    }

    private static void UnregisterMutex(ulong address) => MutexStates.Remove(address);

    private static void EnqueueWaiter(object state, ulong threadId)
    {
        // Mirrors the registration PthreadMutexLockCore performs before parking:
        // queue the handle and account for it in the waiter count.
        var queue = WaiterQueue(state);
        var addLast = queue.GetType().GetMethod("AddLast", [typeof(ulong)])!;
        var waiterAdded = MutexStateType.GetMethod("WaiterAddedLocked")!;
        lock (state)
        {
            _ = addLast.Invoke(queue, [threadId]);
            _ = waiterAdded.Invoke(state, null);
        }
    }

    private static object WaiterQueue(object state) =>
        MutexStateType.GetProperty("WaiterQueue")!.GetValue(state)!;

    private static void SetOwner(object state, ulong threadId, int recursion)
    {
        MutexStateType.GetProperty("OwnerThreadId")!.SetValue(state, threadId);
        MutexStateType.GetProperty("RecursionCount")!.SetValue(state, recursion);
    }

    private static ulong OwnerThreadId(object state) =>
        (ulong)MutexStateType.GetProperty("OwnerThreadId")!.GetValue(state)!;

    private static int RecursionCount(object state) =>
        (int)MutexStateType.GetProperty("RecursionCount")!.GetValue(state)!;

    private static int WaiterQueueLength(object state) =>
        ((ICollection)WaiterQueue(state)).Count;

    private static List<ulong> QueueContents(object state) =>
        ((IEnumerable)WaiterQueue(state)).Cast<ulong>().ToList();
}

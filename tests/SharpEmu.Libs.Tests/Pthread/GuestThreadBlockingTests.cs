// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// GuestThreadBlocking is entirely process-global state: the teardown flag, the
// interrupt set, the parked-thread map and the deliverer delegate are all
// statics shared by every in-place wait in the process. RequestShutdown in
// particular makes every concurrent mutex/condvar/semaphore wait unwind, so
// these run alone in the non-parallel phase rather than racing the pthread
// suites.
[CollectionDefinition(GuestThreadBlockingStateCollection.Name, DisableParallelization = true)]
public sealed class GuestThreadBlockingStateCollection
{
    public const string Name = "GuestThreadBlockingState";
}

[Collection(GuestThreadBlockingStateCollection.Name)]
public sealed class GuestThreadBlockingTests
{
    private const ulong ThreadA = 0x7000_0000_0000_0001;
    private const ulong ThreadB = 0x7000_0000_0000_0002;

    [Fact]
    public void NoteBlockedPublishesParkStateUntilUnblocked()
    {
        Assert.Null(GuestThreadBlocking.DescribeBlock(ThreadA));

        GuestThreadBlocking.NoteBlocked(ThreadA, "pthread_mutex_lock");
        try
        {
            Assert.Equal("pthread_mutex_lock", GuestThreadBlocking.DescribeBlock(ThreadA));
            Assert.Contains(
                GuestThreadBlocking.SnapshotBlockDescriptions(),
                entry => entry.Key == ThreadA && entry.Value == "pthread_mutex_lock");
        }
        finally
        {
            GuestThreadBlocking.NoteUnblocked(ThreadA);
        }

        Assert.Null(GuestThreadBlocking.DescribeBlock(ThreadA));
        Assert.DoesNotContain(GuestThreadBlocking.SnapshotBlockDescriptions(), entry => entry.Key == ThreadA);
    }

    [Fact]
    public void NoteBlockedIgnoresTheZeroHandle()
    {
        // Non-guest callers park with handle 0; they must not pollute the map.
        GuestThreadBlocking.NoteBlocked(0, "pthread_mutex_lock");
        Assert.Null(GuestThreadBlocking.DescribeBlock(0));
        Assert.DoesNotContain(GuestThreadBlocking.SnapshotBlockDescriptions(), entry => entry.Key == 0);
    }

    [Fact]
    public void CheckpointReleasesTheGateAcrossDeliveryAndReacquiresIt()
    {
        var gate = new object();
        var heldDuringDelivery = true;
        var contender = 0;

        using var delivererEntered = new ManualResetEventSlim(false);
        using var contenderTookGate = new ManualResetEventSlim(false);

        WithDeliverer(
            () =>
            {
                // The gate must be free while the handler runs — it may re-enter
                // the very primitive whose gate this is.
                heldDuringDelivery = Monitor.IsEntered(gate);
                delivererEntered.Set();
                contenderTookGate.Wait(TimeSpan.FromSeconds(5));
            },
            () =>
            {
                var contenderThread = new Thread(() =>
                {
                    delivererEntered.Wait(TimeSpan.FromSeconds(5));
                    lock (gate)
                    {
                        Interlocked.Exchange(ref contender, 1);
                    }

                    contenderTookGate.Set();
                })
                { IsBackground = true };

                GuestThreadBlocking.RequestInterrupt(ThreadA);
                lock (gate)
                {
                    contenderThread.Start();
                    GuestThreadBlocking.Checkpoint(ThreadA, gate);

                    // Re-acquired on the way out, so the caller's wait loop can
                    // re-test its predicate under the gate it thinks it holds.
                    Assert.True(Monitor.IsEntered(gate));
                }

                Assert.True(contenderThread.Join(TimeSpan.FromSeconds(5)));
            });

        Assert.False(heldDuringDelivery);
        Assert.Equal(1, Volatile.Read(ref contender));
    }

    [Fact]
    public void CheckpointConsumesAnInterruptExactlyOnce()
    {
        var gate = new object();
        var deliveries = 0;

        WithDeliverer(
            () => Interlocked.Increment(ref deliveries),
            () =>
            {
                GuestThreadBlocking.RequestInterrupt(ThreadA);
                lock (gate)
                {
                    GuestThreadBlocking.Checkpoint(ThreadA, gate);
                    Assert.Equal(1, Volatile.Read(ref deliveries));

                    // Every later checkpoint in the same wait loop must be a
                    // no-op until a fresh interrupt is raised.
                    GuestThreadBlocking.Checkpoint(ThreadA, gate);
                    GuestThreadBlocking.Checkpoint(ThreadA, gate);
                    Assert.Equal(1, Volatile.Read(ref deliveries));
                }
            });
    }

    [Fact]
    public void CheckpointOnlyDeliversToTheInterruptedThread()
    {
        var gate = new object();
        var deliveries = 0;

        WithDeliverer(
            () => Interlocked.Increment(ref deliveries),
            () =>
            {
                GuestThreadBlocking.RequestInterrupt(ThreadA);
                lock (gate)
                {
                    GuestThreadBlocking.Checkpoint(ThreadB, gate);
                    Assert.Equal(0, Volatile.Read(ref deliveries));

                    GuestThreadBlocking.Checkpoint(ThreadA, gate);
                    Assert.Equal(1, Volatile.Read(ref deliveries));
                }
            });
    }

    [Fact]
    public void InterruptSurvivesACheckpointTakenWithNoDelivererInstalled()
    {
        // A wait entered before the first Execute has no deliverer. Consuming
        // the flag there would drop the interrupt silently, and a dropped
        // IL2CPP stop-the-world suspend is a lost wakeup that hangs the guest.
        var gate = new object();
        var previous = GuestThreadBlocking.DeliverInterruptForCurrentThread;
        var deliveries = 0;
        try
        {
            GuestThreadBlocking.DeliverInterruptForCurrentThread = null;
            GuestThreadBlocking.RequestInterrupt(ThreadA);
            lock (gate)
            {
                GuestThreadBlocking.Checkpoint(ThreadA, gate);
            }

            // Deliverer arrives (the backend installs it); the pending interrupt
            // must still be served.
            GuestThreadBlocking.DeliverInterruptForCurrentThread = () => Interlocked.Increment(ref deliveries);
            lock (gate)
            {
                GuestThreadBlocking.Checkpoint(ThreadA, gate);
            }

            Assert.Equal(1, Volatile.Read(ref deliveries));
        }
        finally
        {
            GuestThreadBlocking.DeliverInterruptForCurrentThread = previous;
        }
    }

    [Fact]
    public void ResetShutdownLetsAFreshSessionParkAgain()
    {
        Assert.False(GuestThreadBlocking.ShutdownRequested);
        try
        {
            GuestThreadBlocking.RequestShutdown();
            Assert.True(GuestThreadBlocking.ShutdownRequested);
        }
        finally
        {
            // Without this the flag is set-only and process-wide: every later
            // in-place wait would unwind instead of blocking.
            GuestThreadBlocking.ResetShutdown();
        }

        Assert.False(GuestThreadBlocking.ShutdownRequested);
    }

    /// <summary>
    /// Installs <paramref name="deliverer"/> for the duration of
    /// <paramref name="body"/> and restores the previous one, so a failing
    /// assertion cannot leave a stale delegate behind for the rest of the suite.
    /// </summary>
    private static void WithDeliverer(Action deliverer, Action body)
    {
        var previous = GuestThreadBlocking.DeliverInterruptForCurrentThread;
        GuestThreadBlocking.DeliverInterruptForCurrentThread = deliverer;
        try
        {
            body();
        }
        finally
        {
            GuestThreadBlocking.DeliverInterruptForCurrentThread = previous;
        }
    }
}

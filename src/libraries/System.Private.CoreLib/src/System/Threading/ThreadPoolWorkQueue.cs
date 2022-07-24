// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/*=============================================================================
**
**
**
** Purpose: Class for creating and managing a threadpool
**
**
=============================================================================*/

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace System.Threading
{
    internal sealed partial class ThreadPoolWorkQueue
    {
        internal static class WorkStealingQueueList
        {
#pragma warning disable CA1825 // avoid the extra generic instantiation for Array.Empty<T>(); this is the only place we'll ever create this array
            private static volatile WorkStealingQueue[] _queues = new WorkStealingQueue[0];
#pragma warning restore CA1825

            public static WorkStealingQueue[] Queues => _queues;

            public static void Add(WorkStealingQueue queue)
            {
                Debug.Assert(queue != null);
                while (true)
                {
                    WorkStealingQueue[] oldQueues = _queues;
                    Debug.Assert(Array.IndexOf(oldQueues, queue) < 0);

                    var newQueues = new WorkStealingQueue[oldQueues.Length + 1];
                    Array.Copy(oldQueues, newQueues, oldQueues.Length);
                    newQueues[^1] = queue;
                    if (Interlocked.CompareExchange(ref _queues, newQueues, oldQueues) == oldQueues)
                    {
                        break;
                    }
                }
            }

            public static void Remove(WorkStealingQueue queue)
            {
                Debug.Assert(queue != null);
                while (true)
                {
                    WorkStealingQueue[] oldQueues = _queues;
                    if (oldQueues.Length == 0)
                    {
                        return;
                    }

                    int pos = Array.IndexOf(oldQueues, queue);
                    if (pos < 0)
                    {
                        Debug.Fail("Should have found the queue");
                        return;
                    }

                    var newQueues = new WorkStealingQueue[oldQueues.Length - 1];
                    if (pos == 0)
                    {
                        Array.Copy(oldQueues, 1, newQueues, 0, newQueues.Length);
                    }
                    else if (pos == oldQueues.Length - 1)
                    {
                        Array.Copy(oldQueues, newQueues, newQueues.Length);
                    }
                    else
                    {
                        Array.Copy(oldQueues, newQueues, pos);
                        Array.Copy(oldQueues, pos + 1, newQueues, pos, newQueues.Length - pos);
                    }

                    if (Interlocked.CompareExchange(ref _queues, newQueues, oldQueues) == oldQueues)
                    {
                        break;
                    }
                }
            }
        }

        internal sealed class WorkStealingQueue
        {
            private const int INITIAL_SIZE = 32;
            internal volatile object?[] m_array = new object[INITIAL_SIZE]; // SOS's ThreadPool command depends on this name
            private volatile int m_mask = INITIAL_SIZE - 1;

#if DEBUG
            // in debug builds, start at the end so we exercise the index reset logic.
            private const int START_INDEX = int.MaxValue;
#else
            private const int START_INDEX = 0;
#endif

            private volatile int m_headIndex = START_INDEX;
            private volatile int m_tailIndex = START_INDEX;

            private SpinLock m_foreignLock = new SpinLock(enableThreadOwnerTracking: false);

            public void LocalPush(object obj)
            {
                int tail = m_tailIndex;

                // We're going to increment the tail; if we'll overflow, then we need to reset our counts
                if (tail == int.MaxValue)
                {
                    tail = LocalPush_HandleTailOverflow();
                }

                // When there are at least 2 elements' worth of space, we can take the fast path.
                if (tail < m_headIndex + m_mask)
                {
                    Volatile.Write(ref m_array[tail & m_mask], obj);
                    m_tailIndex = tail + 1;
                }
                else
                {
                    // We need to contend with foreign pops, so we lock.
                    bool lockTaken = false;
                    try
                    {
                        m_foreignLock.Enter(ref lockTaken);

                        int head = m_headIndex;
                        int count = m_tailIndex - m_headIndex;

                        // If there is still space (one left), just add the element.
                        if (count >= m_mask)
                        {
                            // We're full; expand the queue by doubling its size.
                            var newArray = new object?[m_array.Length << 1];
                            for (int i = 0; i < m_array.Length; i++)
                                newArray[i] = m_array[(i + head) & m_mask];

                            // Reset the field values, incl. the mask.
                            m_array = newArray;
                            m_headIndex = 0;
                            m_tailIndex = tail = count;
                            m_mask = (m_mask << 1) | 1;
                        }

                        Volatile.Write(ref m_array[tail & m_mask], obj);
                        m_tailIndex = tail + 1;
                    }
                    finally
                    {
                        if (lockTaken)
                            m_foreignLock.Exit(useMemoryBarrier: false);
                    }
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            private int LocalPush_HandleTailOverflow()
            {
                bool lockTaken = false;
                try
                {
                    m_foreignLock.Enter(ref lockTaken);

                    int tail = m_tailIndex;
                    if (tail == int.MaxValue)
                    {
                        //
                        // Rather than resetting to zero, we'll just mask off the bits we don't care about.
                        // This way we don't need to rearrange the items already in the queue; they'll be found
                        // correctly exactly where they are.  One subtlety here is that we need to make sure that
                        // if head is currently < tail, it remains that way.  This happens to just fall out from
                        // the bit-masking, because we only do this if tail == int.MaxValue, meaning that all
                        // bits are set, so all of the bits we're keeping will also be set.  Thus it's impossible
                        // for the head to end up > than the tail, since you can't set any more bits than all of
                        // them.
                        //
                        m_headIndex &= m_mask;
                        m_tailIndex = tail = m_tailIndex & m_mask;
                        Debug.Assert(m_headIndex <= m_tailIndex);
                    }

                    return tail;
                }
                finally
                {
                    if (lockTaken)
                        m_foreignLock.Exit(useMemoryBarrier: true);
                }
            }

            public bool LocalFindAndPop(object obj)
            {
                // Fast path: check the tail. If equal, we can skip the lock.
                if (m_array[(m_tailIndex - 1) & m_mask] == obj)
                {
                    object? unused = LocalPop();
                    Debug.Assert(unused == null || unused == obj);
                    return unused != null;
                }

                // Else, do an O(N) search for the work item. The theory of work stealing and our
                // inlining logic is that most waits will happen on recently queued work.  And
                // since recently queued work will be close to the tail end (which is where we
                // begin our search), we will likely find it quickly.  In the worst case, we
                // will traverse the whole local queue; this is typically not going to be a
                // problem (although degenerate cases are clearly an issue) because local work
                // queues tend to be somewhat shallow in length, and because if we fail to find
                // the work item, we are about to block anyway (which is very expensive).
                for (int i = m_tailIndex - 2; i >= m_headIndex; i--)
                {
                    if (m_array[i & m_mask] == obj)
                    {
                        // If we found the element, block out steals to avoid interference.
                        bool lockTaken = false;
                        try
                        {
                            m_foreignLock.Enter(ref lockTaken);

                            // If we encountered a race condition, bail.
                            if (m_array[i & m_mask] == null)
                                return false;

                            // Otherwise, null out the element.
                            Volatile.Write(ref m_array[i & m_mask], null);

                            // And then check to see if we can fix up the indexes (if we're at
                            // the edge).  If we can't, we just leave nulls in the array and they'll
                            // get filtered out eventually (but may lead to superfluous resizing).
                            if (i == m_tailIndex)
                                m_tailIndex--;
                            else if (i == m_headIndex)
                                m_headIndex++;

                            return true;
                        }
                        finally
                        {
                            if (lockTaken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }

                return false;
            }

            public object? LocalPop() => m_headIndex < m_tailIndex ? LocalPopCore() : null;

            private object? LocalPopCore()
            {
                while (true)
                {
                    int tail = m_tailIndex;
                    if (m_headIndex >= tail)
                    {
                        return null;
                    }

                    // Decrement the tail using a fence to ensure subsequent read doesn't come before.
                    tail--;
                    Interlocked.Exchange(ref m_tailIndex, tail);

                    // If there is no interaction with a take, we can head down the fast path.
                    if (m_headIndex <= tail)
                    {
                        int idx = tail & m_mask;
                        object? obj = Volatile.Read(ref m_array[idx]);

                        // Check for nulls in the array.
                        if (obj == null) continue;

                        m_array[idx] = null;
                        return obj;
                    }
                    else
                    {
                        // Interaction with takes: 0 or 1 elements left.
                        bool lockTaken = false;
                        try
                        {
                            m_foreignLock.Enter(ref lockTaken);

                            if (m_headIndex <= tail)
                            {
                                // Element still available. Take it.
                                int idx = tail & m_mask;
                                object? obj = Volatile.Read(ref m_array[idx]);

                                // Check for nulls in the array.
                                if (obj == null) continue;

                                m_array[idx] = null;
                                return obj;
                            }
                            else
                            {
                                // If we encountered a race condition and element was stolen, restore the tail.
                                m_tailIndex = tail + 1;
                                return null;
                            }
                        }
                        finally
                        {
                            if (lockTaken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }
            }

            public bool CanSteal => m_headIndex < m_tailIndex;

            public object? TrySteal(ref bool missedSteal)
            {
                while (true)
                {
                    if (CanSteal)
                    {
                        bool taken = false;
                        try
                        {
                            m_foreignLock.TryEnter(ref taken);
                            if (taken)
                            {
                                // Increment head, and ensure read of tail doesn't move before it (fence).
                                int head = m_headIndex;
                                Interlocked.Exchange(ref m_headIndex, head + 1);

                                if (head < m_tailIndex)
                                {
                                    int idx = head & m_mask;
                                    object? obj = Volatile.Read(ref m_array[idx]);

                                    // Check for nulls in the array.
                                    if (obj == null) continue;

                                    m_array[idx] = null;
                                    return obj;
                                }
                                else
                                {
                                    // Failed, restore head.
                                    m_headIndex = head;
                                }
                            }
                        }
                        finally
                        {
                            if (taken)
                                m_foreignLock.Exit(useMemoryBarrier: false);
                        }

                        missedSteal = true;
                    }

                    return null;
                }
            }

            public int Count
            {
                get
                {
                    bool lockTaken = false;
                    try
                    {
                        m_foreignLock.Enter(ref lockTaken);
                        return Math.Max(0, m_tailIndex - m_headIndex);
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            m_foreignLock.Exit(useMemoryBarrier: false);
                        }
                    }
                }
            }
        }

        private const int ProcessorsPerAssignableWorkItemQueue = 16;
        private static readonly int s_assignableWorkItemQueueCount =
            Environment.ProcessorCount <= 32 ? 0 :
                (Environment.ProcessorCount + (ProcessorsPerAssignableWorkItemQueue - 1)) / ProcessorsPerAssignableWorkItemQueue;

        private bool _loggingEnabled;
        private bool _dispatchNormalPriorityWorkFirst;
        private int _mayHaveHighPriorityWorkItems;

        // SOS's ThreadPool command depends on the following names
        internal readonly ConcurrentQueue<object> workItems = new ConcurrentQueue<object>();
        internal readonly ConcurrentQueue<object> highPriorityWorkItems = new ConcurrentQueue<object>();

        // SOS's ThreadPool command depends on the following name. The global queue doesn't scale well beyond a point of
        // concurrency. Some additional queues may be added and assigned to a limited number of worker threads if necessary to
        // help with limiting the concurrency level.
        internal readonly ConcurrentQueue<object>[] _assignableWorkItemQueues =
            new ConcurrentQueue<object>[s_assignableWorkItemQueueCount];

        private readonly LowLevelLock _queueAssignmentLock = new();
        private readonly int[] _assignedWorkItemQueueThreadCounts =
            s_assignableWorkItemQueueCount > 0 ? new int[s_assignableWorkItemQueueCount] : Array.Empty<int>();

        [StructLayout(LayoutKind.Sequential)]
        private struct CacheLineSeparated
        {
            private readonly Internal.PaddingFor32 pad1;

            public int hasOutstandingThreadRequest;

            private readonly Internal.PaddingFor32 pad2;
        }

        private CacheLineSeparated _separated;

        public ThreadPoolWorkQueue()
        {
            for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
            {
                _assignableWorkItemQueues[i] = new ConcurrentQueue<object>();
            }

            RefreshLoggingEnabled();
        }

        private void AssignWorkItemQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            Debug.Assert(s_assignableWorkItemQueueCount > 0);

            _queueAssignmentLock.Acquire();

            // Determine the first queue that has not yet been assigned to the limit of worker threads
            int queueIndex = -1;
            int minCount = int.MaxValue;
            int minCountQueueIndex = 0;
            for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
            {
                int count = _assignedWorkItemQueueThreadCounts[i];
                Debug.Assert(count >= 0);
                if (count < ProcessorsPerAssignableWorkItemQueue)
                {
                    queueIndex = i;
                    _assignedWorkItemQueueThreadCounts[queueIndex] = count + 1;
                    break;
                }

                if (count < minCount)
                {
                    minCount = count;
                    minCountQueueIndex = i;
                }
            }

            if (queueIndex < 0)
            {
                // All queues have been fully assigned. Choose the queue that has been assigned to the least number of worker
                // threads.
                queueIndex = minCountQueueIndex;
                _assignedWorkItemQueueThreadCounts[queueIndex]++;
            }

            _queueAssignmentLock.Release();

            tl.queueIndex = queueIndex;
            tl.assignedGlobalWorkItemQueue = _assignableWorkItemQueues[queueIndex];
        }

        private void TryReassignWorkItemQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            Debug.Assert(s_assignableWorkItemQueueCount > 0);

            int queueIndex = tl.queueIndex;
            if (queueIndex == 0)
            {
                return;
            }

            if (!_queueAssignmentLock.TryAcquire())
            {
                return;
            }

            // If the currently assigned queue is assigned to other worker threads, try to reassign an earlier queue to this
            // worker thread if the earlier queue is not assigned to the limit of worker threads
            Debug.Assert(_assignedWorkItemQueueThreadCounts[queueIndex] >= 0);
            if (_assignedWorkItemQueueThreadCounts[queueIndex] > 1)
            {
                for (int i = 0; i < queueIndex; i++)
                {
                    if (_assignedWorkItemQueueThreadCounts[i] < ProcessorsPerAssignableWorkItemQueue)
                    {
                        _assignedWorkItemQueueThreadCounts[queueIndex]--;
                        queueIndex = i;
                        _assignedWorkItemQueueThreadCounts[queueIndex]++;
                        break;
                    }
                }
            }

            _queueAssignmentLock.Release();

            tl.queueIndex = queueIndex;
            tl.assignedGlobalWorkItemQueue = _assignableWorkItemQueues[queueIndex];
        }

        private void UnassignWorkItemQueue(ThreadPoolWorkQueueThreadLocals tl)
        {
            Debug.Assert(s_assignableWorkItemQueueCount > 0);

            int queueIndex = tl.queueIndex;

            _queueAssignmentLock.Acquire();
            int newCount = --_assignedWorkItemQueueThreadCounts[queueIndex];
            _queueAssignmentLock.Release();

            Debug.Assert(newCount >= 0);
            if (newCount > 0)
            {
                return;
            }

            // This queue is not assigned to any other worker threads. Move its work items to the global queue to prevent them
            // from being starved for a long duration.
            bool movedWorkItem = false;
            ConcurrentQueue<object> queue = tl.assignedGlobalWorkItemQueue;
            while (_assignedWorkItemQueueThreadCounts[queueIndex] <= 0 && queue.TryDequeue(out object? workItem))
            {
                workItems.Enqueue(workItem);
                movedWorkItem = true;
            }

            if (movedWorkItem)
            {
                EnsureThreadRequested();
            }
        }

        public ThreadPoolWorkQueueThreadLocals GetOrCreateThreadLocals() =>
            ThreadPoolWorkQueueThreadLocals.threadLocals ?? CreateThreadLocals();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ThreadPoolWorkQueueThreadLocals CreateThreadLocals()
        {
            Debug.Assert(ThreadPoolWorkQueueThreadLocals.threadLocals == null);

            return ThreadPoolWorkQueueThreadLocals.threadLocals = new ThreadPoolWorkQueueThreadLocals(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefreshLoggingEnabled()
        {
            if (!FrameworkEventSource.Log.IsEnabled())
            {
                if (_loggingEnabled)
                {
                    _loggingEnabled = false;
                }
                return;
            }

            RefreshLoggingEnabledFull();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RefreshLoggingEnabledFull()
        {
            _loggingEnabled = FrameworkEventSource.Log.IsEnabled(EventLevel.Verbose, FrameworkEventSource.Keywords.ThreadPool | FrameworkEventSource.Keywords.ThreadTransfer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void EnsureThreadRequested()
        {
            // Only one thread is requested at a time to avoid over-parallelization
            if (Interlocked.CompareExchange(ref _separated.hasOutstandingThreadRequest, 1, 0) == 0)
            {
                ThreadPool.RequestWorkerThread();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void MarkThreadRequestSatisfied()
        {
            // The change needs to be visible to other threads that may request a worker thread before a work item is attempted
            // to be dequeued by the current thread. In particular, if an enqueuer queues a work item and does not request a
            // thread because it sees that a thread request is already outstanding, and the current thread is the last thread
            // processing work items, the current thread must see the work item queued by the enqueuer.
            _separated.hasOutstandingThreadRequest = 0;
            Interlocked.MemoryBarrier();
        }

        public void Enqueue(object callback, bool forceGlobal)
        {
            Debug.Assert((callback is IThreadPoolWorkItem) ^ (callback is Task));

            if (_loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(callback);

            ThreadPoolWorkQueueThreadLocals? tl;
            if (!forceGlobal && (tl = ThreadPoolWorkQueueThreadLocals.threadLocals) != null)
            {
                tl.workStealingQueue.LocalPush(callback);
            }
            else
            {
                ConcurrentQueue<object> queue =
                    s_assignableWorkItemQueueCount > 0 && (tl = ThreadPoolWorkQueueThreadLocals.threadLocals) != null
                        ? tl.assignedGlobalWorkItemQueue
                        : workItems;
                queue.Enqueue(callback);
            }

            EnsureThreadRequested();
        }

        public void EnqueueAtHighPriority(object workItem)
        {
            Debug.Assert((workItem is IThreadPoolWorkItem) ^ (workItem is Task));

            if (_loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(workItem);

            highPriorityWorkItems.Enqueue(workItem);

            // If the change below is seen by another thread, ensure that the enqueued work item will also be visible
            Volatile.Write(ref _mayHaveHighPriorityWorkItems, 1);

            EnsureThreadRequested();
        }

        internal static bool LocalFindAndPop(object callback)
        {
            ThreadPoolWorkQueueThreadLocals? tl = ThreadPoolWorkQueueThreadLocals.threadLocals;
            return tl != null && tl.workStealingQueue.LocalFindAndPop(callback);
        }

        public object? Dequeue(ThreadPoolWorkQueueThreadLocals tl, ref bool missedSteal)
        {
            // Check for local work items
            object? workItem = tl.workStealingQueue.LocalPop();
            if (workItem != null)
            {
                return workItem;
            }

            // Check for high-priority work items
            if (tl.isProcessingHighPriorityWorkItems)
            {
                if (highPriorityWorkItems.TryDequeue(out workItem))
                {
                    return workItem;
                }

                tl.isProcessingHighPriorityWorkItems = false;
            }
            else if (
                _mayHaveHighPriorityWorkItems != 0 &&
                Interlocked.CompareExchange(ref _mayHaveHighPriorityWorkItems, 0, 1) != 0 &&
                TryStartProcessingHighPriorityWorkItemsAndDequeue(tl, out workItem))
            {
                return workItem;
            }

            // Check for work items from the assigned global queue
            if (s_assignableWorkItemQueueCount > 0 && tl.assignedGlobalWorkItemQueue.TryDequeue(out workItem))
            {
                return workItem;
            }

            // Check for work items from the global queue
            if (workItems.TryDequeue(out workItem))
            {
                return workItem;
            }

            // Check for work items in other assignable global queues
            uint randomValue = tl.random.NextUInt32();
            if (s_assignableWorkItemQueueCount > 0)
            {
                int queueIndex = tl.queueIndex;
                int c = s_assignableWorkItemQueueCount;
                int maxIndex = c - 1;
                for (int i = (int)(randomValue % (uint)c); c > 0; i = i < maxIndex ? i + 1 : 0, c--)
                {
                    if (i != queueIndex && _assignableWorkItemQueues[i].TryDequeue(out workItem))
                    {
                        return workItem;
                    }
                }
            }

            // Try to steal from other threads' local work items
            {
                WorkStealingQueue localWsq = tl.workStealingQueue;
                WorkStealingQueue[] queues = WorkStealingQueueList.Queues;
                int c = queues.Length;
                Debug.Assert(c > 0, "There must at least be a queue for this thread.");
                int maxIndex = c - 1;
                for (int i = (int)(randomValue % (uint)c); c > 0; i = i < maxIndex ? i + 1 : 0, c--)
                {
                    WorkStealingQueue otherQueue = queues[i];
                    if (otherQueue != localWsq && otherQueue.CanSteal)
                    {
                        workItem = otherQueue.TrySteal(ref missedSteal);
                        if (workItem != null)
                        {
                            return workItem;
                        }
                    }
                }
            }

            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryStartProcessingHighPriorityWorkItemsAndDequeue(
            ThreadPoolWorkQueueThreadLocals tl,
            [MaybeNullWhen(false)] out object workItem)
        {
            Debug.Assert(!tl.isProcessingHighPriorityWorkItems);

            if (!highPriorityWorkItems.TryDequeue(out workItem))
            {
                return false;
            }

            tl.isProcessingHighPriorityWorkItems = true;
            _mayHaveHighPriorityWorkItems = 1;
            return true;
        }

        public static long LocalCount
        {
            get
            {
                long count = 0;
                foreach (WorkStealingQueue workStealingQueue in WorkStealingQueueList.Queues)
                {
                    count += workStealingQueue.Count;
                }
                return count;
            }
        }

        public long GlobalCount
        {
            get
            {
                long count = (long)highPriorityWorkItems.Count + workItems.Count;
                for (int i = 0; i < s_assignableWorkItemQueueCount; i++)
                {
                    count += _assignableWorkItemQueues[i].Count;
                }

                return count;
            }
        }

        // Time in ms for which ThreadPoolWorkQueue.Dispatch keeps executing normal work items before either returning from
        // Dispatch (if YieldFromDispatchLoop is true), or performing periodic activities
        public const uint DispatchQuantumMs = 30;

        /// <summary>
        /// Dispatches work items to this thread.
        /// </summary>
        /// <returns>
        /// <c>true</c> if this thread did as much work as was available or its quantum expired.
        /// <c>false</c> if this thread stopped working early.
        /// </returns>
        internal static bool Dispatch()
        {
            ThreadPoolWorkQueue workQueue = ThreadPool.s_workQueue;
            ThreadPoolWorkQueueThreadLocals tl = workQueue.GetOrCreateThreadLocals();

            if (s_assignableWorkItemQueueCount > 0)
            {
                workQueue.AssignWorkItemQueue(tl);
            }

            // Before dequeuing the first work item, acknowledge that the thread request has been satisfied
            workQueue.MarkThreadRequestSatisfied();

            object? workItem = null;
            {
                // Alternate between checking for high-prioriy and normal-priority work first, that way both sets of work
                // items get a chance to run in situations where worker threads are starved and work items that run also
                // take over the thread, sustaining starvation. For example, when worker threads are continually starved,
                // high-priority work items may always be queued and normal-priority work items may not get a chance to run.
                bool dispatchNormalPriorityWorkFirst = workQueue._dispatchNormalPriorityWorkFirst;
                if (dispatchNormalPriorityWorkFirst && !tl.workStealingQueue.CanSteal)
                {
                    workQueue._dispatchNormalPriorityWorkFirst = !dispatchNormalPriorityWorkFirst;
                    ConcurrentQueue<object> queue =
                        s_assignableWorkItemQueueCount > 0 ? tl.assignedGlobalWorkItemQueue : workQueue.workItems;
                    if (!queue.TryDequeue(out workItem) && s_assignableWorkItemQueueCount > 0)
                    {
                        workQueue.workItems.TryDequeue(out workItem);
                    }
                }

                if (workItem == null)
                {
                    bool missedSteal = false;
                    workItem = workQueue.Dequeue(tl, ref missedSteal);

                    if (workItem == null)
                    {
                        if (s_assignableWorkItemQueueCount > 0)
                        {
                            workQueue.UnassignWorkItemQueue(tl);
                        }

                        //
                        // No work.
                        // If we missed a steal, though, there may be more work in the queue.
                        // Instead of looping around and trying again, we'll just request another thread.  Hopefully the thread
                        // that owns the contended work-stealing queue will pick up its own workitems in the meantime,
                        // which will be more efficient than this thread doing it anyway.
                        //
                        if (missedSteal)
                        {
                            workQueue.EnsureThreadRequested();
                        }

                        // Tell the VM we're returning normally, not because Hill Climbing asked us to return.
                        return true;
                    }
                }

                // A work item was successfully dequeued, and there may be more work items to process. Request a thread to
                // parallelize processing of work items, before processing more work items. Following this, it is the
                // responsibility of the new thread and other enqueuers to request more threads as necessary. The
                // parallelization may be necessary here for correctness (aside from perf) if the work item blocks for some
                // reason that may have a dependency on other queued work items.
                workQueue.EnsureThreadRequested();

                // After this point, this method is no longer responsible for ensuring thread requests except for missed steals
            }

            // Has the desire for logging changed since the last time we entered?
            workQueue.RefreshLoggingEnabled();

            object? threadLocalCompletionCountObject = tl.threadLocalCompletionCountObject;
            Thread currentThread = tl.currentThread;

            // Start on clean ExecutionContext and SynchronizationContext
            currentThread._executionContext = null;
            currentThread._synchronizationContext = null;

            //
            // Save the start time
            //
            int startTickCount = Environment.TickCount;

            //
            // Loop until our quantum expires or there is no work.
            //
            while (true)
            {
                if (workItem == null)
                {
                    bool missedSteal = false;
                    workItem = workQueue.Dequeue(tl, ref missedSteal);

                    if (workItem == null)
                    {
                        if (s_assignableWorkItemQueueCount > 0)
                        {
                            workQueue.UnassignWorkItemQueue(tl);
                        }

                        //
                        // No work.
                        // If we missed a steal, though, there may be more work in the queue.
                        // Instead of looping around and trying again, we'll just request another thread.  Hopefully the thread
                        // that owns the contended work-stealing queue will pick up its own workitems in the meantime,
                        // which will be more efficient than this thread doing it anyway.
                        //
                        if (missedSteal)
                        {
                            workQueue.EnsureThreadRequested();
                        }

                        return true;
                    }
                }

                if (workQueue._loggingEnabled && FrameworkEventSource.Log.IsEnabled())
                {
                    FrameworkEventSource.Log.ThreadPoolDequeueWorkObject(workItem);
                }

                //
                // Execute the workitem outside of any finally blocks, so that it can be aborted if needed.
                //
#if FEATURE_OBJCMARSHAL
                if (AutoreleasePool.EnableAutoreleasePool)
                {
                    DispatchItemWithAutoreleasePool(workItem, currentThread);
                }
                else
#endif
#pragma warning disable CS0162 // Unreachable code detected. EnableWorkerTracking may be a constant in some runtimes.
                if (ThreadPool.EnableWorkerTracking)
                {
                    DispatchWorkItemWithWorkerTracking(workItem, currentThread);
                }
                else
                {
                    DispatchWorkItem(workItem, currentThread);
                }
#pragma warning restore CS0162

                // Release refs
                workItem = null;

                // Return to clean ExecutionContext and SynchronizationContext. This may call user code (AsyncLocal value
                // change notifications).
                ExecutionContext.ResetThreadPoolThread(currentThread);

                // Reset thread state after all user code for the work item has completed
                currentThread.ResetThreadPoolThread();

                //
                // Notify the VM that we executed this workitem.  This is also our opportunity to ask whether Hill Climbing wants
                // us to return the thread to the pool or not.
                //
                int currentTickCount = Environment.TickCount;
                if (!ThreadPool.NotifyWorkItemComplete(threadLocalCompletionCountObject, currentTickCount))
                {
                    // This thread is being parked and may remain inactive for a while. Transfer any thread-local work items
                    // to ensure that they would not be heavily delayed. Tell the caller that this thread was requested to stop
                    // processing work items.
                    tl.TransferLocalWork();
                    tl.isProcessingHighPriorityWorkItems = false;
                    if (s_assignableWorkItemQueueCount > 0)
                    {
                        workQueue.UnassignWorkItemQueue(tl);
                    }
                    return false;
                }

                // Check if the dispatch quantum has expired
                if ((uint)(currentTickCount - startTickCount) < DispatchQuantumMs)
                {
                    continue;
                }

                // The quantum expired, do any necessary periodic activities

                if (ThreadPool.YieldFromDispatchLoop)
                {
                    // The runtime-specific thread pool implementation requires the Dispatch loop to return to the VM
                    // periodically to let it perform its own work
                    tl.isProcessingHighPriorityWorkItems = false;
                    if (s_assignableWorkItemQueueCount > 0)
                    {
                        workQueue.UnassignWorkItemQueue(tl);
                    }
                    return true;
                }

                if (s_assignableWorkItemQueueCount > 0)
                {
                    // Due to hill climbing, over time arbitrary worker threads may stop working and eventually unbalance the
                    // queue assignments. Periodically try to reassign a queue to keep the assigned queues busy.
                    workQueue.TryReassignWorkItemQueue(tl);
                }

                // This method will continue to dispatch work items. Refresh the start tick count for the next dispatch
                // quantum and do some periodic activities.
                startTickCount = currentTickCount;

                // Periodically refresh whether logging is enabled
                workQueue.RefreshLoggingEnabled();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DispatchWorkItemWithWorkerTracking(object workItem, Thread currentThread)
        {
            Debug.Assert(ThreadPool.EnableWorkerTracking);
            Debug.Assert(currentThread == Thread.CurrentThread);

            bool reportedStatus = false;
            try
            {
                ThreadPool.ReportThreadStatus(isWorking: true);
                reportedStatus = true;
                DispatchWorkItem(workItem, currentThread);
            }
            finally
            {
                if (reportedStatus)
                    ThreadPool.ReportThreadStatus(isWorking: false);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DispatchWorkItem(object workItem, Thread currentThread)
        {
            if (workItem is Task task)
            {
                task.ExecuteFromThreadPool(currentThread);
            }
            else
            {
                Debug.Assert(workItem is IThreadPoolWorkItem);
                Unsafe.As<IThreadPoolWorkItem>(workItem).Execute();
            }
        }
    }

    // Holds a WorkStealingQueue, and removes it from the list when this object is no longer referenced.
    internal sealed class ThreadPoolWorkQueueThreadLocals
    {
        [ThreadStatic]
        public static ThreadPoolWorkQueueThreadLocals? threadLocals;

        public bool isProcessingHighPriorityWorkItems;
        public int queueIndex;
        public ConcurrentQueue<object> assignedGlobalWorkItemQueue;
        public readonly ThreadPoolWorkQueue workQueue;
        public readonly ThreadPoolWorkQueue.WorkStealingQueue workStealingQueue;
        public readonly Thread currentThread;
        public readonly object? threadLocalCompletionCountObject;
        public readonly Random.XoshiroImpl random = new Random.XoshiroImpl();

        public ThreadPoolWorkQueueThreadLocals(ThreadPoolWorkQueue tpq)
        {
            assignedGlobalWorkItemQueue = tpq.workItems;
            workQueue = tpq;
            workStealingQueue = new ThreadPoolWorkQueue.WorkStealingQueue();
            ThreadPoolWorkQueue.WorkStealingQueueList.Add(workStealingQueue);
            currentThread = Thread.CurrentThread;
            threadLocalCompletionCountObject = ThreadPool.GetOrCreateThreadLocalCompletionCountObject();
        }

        public void TransferLocalWork()
        {
            while (workStealingQueue.LocalPop() is object cb)
            {
                workQueue.Enqueue(cb, forceGlobal: true);
            }
        }

        ~ThreadPoolWorkQueueThreadLocals()
        {
            // Transfer any pending workitems into the global queue so that they will be executed by another thread
            if (null != workStealingQueue)
            {
                TransferLocalWork();
                ThreadPoolWorkQueue.WorkStealingQueueList.Remove(workStealingQueue);
            }
        }
    }

    // A strongly typed callback for ThreadPoolTypedWorkItemQueue<T, TCallback>.
    // This way we avoid the indirection of a delegate call.
    internal interface IThreadPoolTypedWorkItemQueueCallback<T>
    {
        static abstract void Invoke(T item);
    }

    internal sealed class ThreadPoolTypedWorkItemQueue<T, TCallback> : IThreadPoolWorkItem
        // https://github.com/dotnet/runtime/pull/69278#discussion_r871927939
        where T : struct
        where TCallback : struct, IThreadPoolTypedWorkItemQueueCallback<T>
    {
        private int _isScheduledForProcessing;
        private readonly ConcurrentQueue<T> _workItems = new ConcurrentQueue<T>();

        public int Count => _workItems.Count;

        public void Enqueue(T workItem)
        {
            BatchEnqueue(workItem);
            CompleteBatchEnqueue();
        }

        public void BatchEnqueue(T workItem) => _workItems.Enqueue(workItem);
        public void CompleteBatchEnqueue() => ScheduleForProcessing();

        private void ScheduleForProcessing()
        {
            // Only one thread is requested at a time to avoid over-parallelization. Currently where this type is used, queued
            // work is expected to be processed at high priority. The implementation could be modified to support different
            // priorities if necessary.
            if (Interlocked.CompareExchange(ref _isScheduledForProcessing, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueHighPriorityWorkItemInternal(this);
            }
        }

        void IThreadPoolWorkItem.Execute()
        {
            Debug.Assert(_isScheduledForProcessing != 0);

            // This change needs to be visible to other threads that may enqueue work items before a work item is attempted to
            // be dequeued by the current thread. In particular, if an enqueuer queues a work item and does not schedule for
            // processing, and the current thread is the last thread processing work items, the current thread must see the work
            // item queued by the enqueuer.
            _isScheduledForProcessing = 0;
            Interlocked.MemoryBarrier();
            if (!_workItems.TryDequeue(out T workItem))
            {
                return;
            }

            // An work item was successfully dequeued, and there may be more work items to process. Schedule a work item to
            // parallelize processing of work items, before processing more work items. Following this, it is the responsibility
            // of the new work item and the poller thread to schedule more work items as necessary. The parallelization may be
            // necessary here if the user callback as part of handling the work item blocks for some reason that may have a
            // dependency on other queued work items.
            ScheduleForProcessing();

            ThreadPoolWorkQueueThreadLocals tl = ThreadPoolWorkQueueThreadLocals.threadLocals!;
            Debug.Assert(tl != null);
            Thread currentThread = tl.currentThread;
            Debug.Assert(currentThread == Thread.CurrentThread);
            uint completedCount = 0;
            int startTimeMs = Environment.TickCount;
            while (true)
            {
                TCallback.Invoke(workItem);

                // This work item processes queued work items until certain conditions are met, and tracks some things:
                // - Keep track of the number of work items processed, it will be added to the counter later
                // - Local work items take precedence over all other types of work items, process them first
                // - This work item should not run for too long. It is processing a specific type of work in batch, but should
                //   not starve other thread pool work items. Check how long it has been since this work item has started, and
                //   yield to the thread pool after some time. The threshold used is half of the thread pool's dispatch quantum,
                //   which the thread pool uses for doing periodic work.
                if (++completedCount == uint.MaxValue ||
                    tl.workStealingQueue.CanSteal ||
                    (uint)(Environment.TickCount - startTimeMs) >= ThreadPoolWorkQueue.DispatchQuantumMs / 2 ||
                    !_workItems.TryDequeue(out workItem))
                {
                    break;
                }

                // Return to clean ExecutionContext and SynchronizationContext. This may call user code (AsyncLocal value
                // change notifications).
                ExecutionContext.ResetThreadPoolThread(currentThread);

                // Reset thread state after all user code for the work item has completed
                currentThread.ResetThreadPoolThread();
            }

            ThreadInt64PersistentCounter.Add(tl.threadLocalCompletionCountObject!, completedCount);
        }
    }

    public delegate void WaitCallback(object? state);

    public delegate void WaitOrTimerCallback(object? state, bool timedOut);  // signaled or timed out

    internal abstract class QueueUserWorkItemCallbackBase : IThreadPoolWorkItem
    {
#if DEBUG
        private int executed;

        ~QueueUserWorkItemCallbackBase()
        {
            Interlocked.MemoryBarrier(); // ensure that an old cached value is not read below
            Debug.Assert(
                executed != 0, "A QueueUserWorkItemCallback was never called!");
        }
#endif

        public virtual void Execute()
        {
#if DEBUG
            GC.SuppressFinalize(this);
            Debug.Assert(
                0 == Interlocked.Exchange(ref executed, 1),
                "A QueueUserWorkItemCallback was called twice!");
#endif
        }
    }

    internal sealed class QueueUserWorkItemCallback : QueueUserWorkItemCallbackBase
    {
        private WaitCallback? _callback; // SOS's ThreadPool command depends on this name
        private readonly object? _state;
        private readonly ExecutionContext _context;

        private static readonly Action<QueueUserWorkItemCallback> s_executionContextShim = quwi =>
        {
            Debug.Assert(quwi._callback != null);
            WaitCallback callback = quwi._callback;
            quwi._callback = null;

            callback(quwi._state);
        };

        internal QueueUserWorkItemCallback(WaitCallback callback, object? state, ExecutionContext context)
        {
            Debug.Assert(context != null);

            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void Execute()
        {
            base.Execute();

            ExecutionContext.RunForThreadPoolUnsafe(_context, s_executionContextShim, this);
        }
    }

    internal sealed class QueueUserWorkItemCallback<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState>? _callback; // SOS's ThreadPool command depends on this name
        private readonly TState _state;
        private readonly ExecutionContext _context;

        internal QueueUserWorkItemCallback(Action<TState> callback, TState state, ExecutionContext context)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void Execute()
        {
            base.Execute();

            Debug.Assert(_callback != null);
            Action<TState> callback = _callback;
            _callback = null;

            ExecutionContext.RunForThreadPoolUnsafe(_context, callback, in _state);
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext : QueueUserWorkItemCallbackBase
    {
        private WaitCallback? _callback; // SOS's ThreadPool command depends on this name
        private readonly object? _state;

        internal QueueUserWorkItemCallbackDefaultContext(WaitCallback callback, object? state)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
        }

        public override void Execute()
        {
            ExecutionContext.CheckThreadPoolAndContextsAreDefault();
            base.Execute();

            Debug.Assert(_callback != null);
            WaitCallback callback = _callback;
            _callback = null;

            callback(_state);

            // ThreadPoolWorkQueue.Dispatch will handle notifications and reset EC and SyncCtx back to default
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState>? _callback; // SOS's ThreadPool command depends on this name
        private readonly TState _state;

        internal QueueUserWorkItemCallbackDefaultContext(Action<TState> callback, TState state)
        {
            Debug.Assert(callback != null);

            _callback = callback;
            _state = state;
        }

        public override void Execute()
        {
            ExecutionContext.CheckThreadPoolAndContextsAreDefault();
            base.Execute();

            Debug.Assert(_callback != null);
            Action<TState> callback = _callback;
            _callback = null;

            callback(_state);

            // ThreadPoolWorkQueue.Dispatch will handle notifications and reset EC and SyncCtx back to default
        }
    }

    internal sealed class _ThreadPoolWaitOrTimerCallback
    {
        private readonly WaitOrTimerCallback _waitOrTimerCallback;
        private readonly ExecutionContext? _executionContext;
        private readonly object? _state;
        private static readonly ContextCallback _ccbt = new ContextCallback(WaitOrTimerCallback_Context_t);
        private static readonly ContextCallback _ccbf = new ContextCallback(WaitOrTimerCallback_Context_f);

        internal _ThreadPoolWaitOrTimerCallback(WaitOrTimerCallback waitOrTimerCallback, object? state, bool flowExecutionContext)
        {
            _waitOrTimerCallback = waitOrTimerCallback;
            _state = state;

            if (flowExecutionContext)
            {
                // capture the exection context
                _executionContext = ExecutionContext.Capture();
            }
        }

        private static void WaitOrTimerCallback_Context_t(object? state) =>
            WaitOrTimerCallback_Context(state, timedOut: true);

        private static void WaitOrTimerCallback_Context_f(object? state) =>
            WaitOrTimerCallback_Context(state, timedOut: false);

        private static void WaitOrTimerCallback_Context(object? state, bool timedOut)
        {
            _ThreadPoolWaitOrTimerCallback helper = (_ThreadPoolWaitOrTimerCallback)state!;
            helper._waitOrTimerCallback(helper._state, timedOut);
        }

        // call back helper
        internal static void PerformWaitOrTimerCallback(_ThreadPoolWaitOrTimerCallback helper, bool timedOut)
        {
            Debug.Assert(helper != null, "Null state passed to PerformWaitOrTimerCallback!");
            // call directly if it is an unsafe call OR EC flow is suppressed
            ExecutionContext? context = helper._executionContext;
            if (context == null)
            {
                WaitOrTimerCallback callback = helper._waitOrTimerCallback;
                callback(helper._state, timedOut);
            }
            else
            {
                ExecutionContext.Run(context, timedOut ? _ccbt : _ccbf, helper);
            }
        }
    }

    public static partial class ThreadPool
    {
        internal const string WorkerThreadName = ".NET ThreadPool Worker";

        internal static readonly ThreadPoolWorkQueue s_workQueue = new ThreadPoolWorkQueue();

        /// <summary>Shim used to invoke <see cref="IAsyncStateMachineBox.MoveNext"/> of the supplied <see cref="IAsyncStateMachineBox"/>.</summary>
        internal static readonly Action<object?> s_invokeAsyncStateMachineBox = static state =>
        {
            if (!(state is IAsyncStateMachineBox box))
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.state);
                return;
            }

            box.MoveNext();
        };

        internal static bool EnableWorkerTracking => IsWorkerTrackingEnabledInConfig && EventSource.IsSupported;

        [CLSCompliant(false)]
        public static RegisteredWaitHandle RegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval > (uint)int.MaxValue && millisecondsTimeOutInterval != uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        [CLSCompliant(false)]
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval > (uint)int.MaxValue && millisecondsTimeOutInterval != uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        public static RegisteredWaitHandle RegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             object? state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        public static RegisteredWaitHandle RegisterWaitForSingleObject(
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            object? state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (millisecondsTimeOutInterval > (uint)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            object? state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (millisecondsTimeOutInterval > (uint)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        public static RegisteredWaitHandle RegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          object? state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
            long tm = (long)timeout.TotalMilliseconds;
            if (tm < -1)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (tm > (long)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)tm, executeOnlyOnce, true);
        }

        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          object? state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
            long tm = (long)timeout.TotalMilliseconds;
            if (tm < -1)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (tm > (long)int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (uint)tm, executeOnlyOnce, false);
        }

        public static bool QueueUserWorkItem(WaitCallback callBack) =>
            QueueUserWorkItem(callBack, null);

        public static bool QueueUserWorkItem(WaitCallback callBack, object? state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            ExecutionContext? context = ExecutionContext.Capture();

            object tpcallBack = (context == null || context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext(callBack!, state) :
                (object)new QueueUserWorkItemCallback(callBack!, state, context);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        public static bool QueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            ExecutionContext? context = ExecutionContext.Capture();

            object tpcallBack = (context == null || context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext<TState>(callBack!, state) :
                (object)new QueueUserWorkItemCallback<TState>(callBack!, state, context);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: !preferLocal);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            // If the callback is the runtime-provided invocation of an IAsyncStateMachineBox,
            // then we can queue the Task state directly to the ThreadPool instead of
            // wrapping it in a QueueUserWorkItemCallback.
            //
            // This occurs when user code queues its provided continuation to the ThreadPool;
            // internally we call UnsafeQueueUserWorkItemInternal directly for Tasks.
            if (ReferenceEquals(callBack, ThreadPool.s_invokeAsyncStateMachineBox))
            {
                if (!(state is IAsyncStateMachineBox))
                {
                    // The provided state must be the internal IAsyncStateMachineBox (Task) type
                    ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.state);
                }

                UnsafeQueueUserWorkItemInternal((object)state!, preferLocal);
                return true;
            }

            s_workQueue.Enqueue(
                new QueueUserWorkItemCallbackDefaultContext<TState>(callBack!, state), forceGlobal: !preferLocal);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem(WaitCallback callBack, object? state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            object tpcallBack = new QueueUserWorkItemCallbackDefaultContext(callBack!, state);

            s_workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem(IThreadPoolWorkItem callBack, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }
            if (callBack is Task)
            {
                // Prevent code from queueing a derived Task that also implements the interface,
                // as that would bypass Task.Start and its safety checks.
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.callBack);
            }

            UnsafeQueueUserWorkItemInternal(callBack!, preferLocal);
            return true;
        }

        internal static void UnsafeQueueUserWorkItemInternal(object callBack, bool preferLocal) =>
            s_workQueue.Enqueue(callBack, forceGlobal: !preferLocal);
        internal static void UnsafeQueueHighPriorityWorkItemInternal(IThreadPoolWorkItem callBack) =>
            s_workQueue.EnqueueAtHighPriority(callBack);

        // This method tries to take the target callback out of the current thread's queue.
        internal static bool TryPopCustomWorkItem(object workItem)
        {
            Debug.Assert(null != workItem);
            return ThreadPoolWorkQueue.LocalFindAndPop(workItem);
        }

        // Get all workitems.  Called by TaskScheduler in its debugger hooks.
        internal static IEnumerable<object> GetQueuedWorkItems()
        {
            // Enumerate high-priority queue
            foreach (object workItem in s_workQueue.highPriorityWorkItems)
            {
                yield return workItem;
            }

            // Enumerate assignable global queues
            foreach (ConcurrentQueue<object> queue in s_workQueue._assignableWorkItemQueues)
            {
                foreach (object workItem in queue)
                {
                    yield return workItem;
                }
            }

            // Enumerate global queue
            foreach (object workItem in s_workQueue.workItems)
            {
                yield return workItem;
            }

            // Enumerate each local queue
            foreach (ThreadPoolWorkQueue.WorkStealingQueue wsq in ThreadPoolWorkQueue.WorkStealingQueueList.Queues)
            {
                if (wsq != null && wsq.m_array != null)
                {
                    object?[] items = wsq.m_array;
                    for (int i = 0; i < items.Length; i++)
                    {
                        object? item = items[i];
                        if (item != null)
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the number of work items that are currently queued to be processed.
        /// </summary>
        /// <remarks>
        /// For a thread pool implementation that may have different types of work items, the count includes all types that can
        /// be tracked, which may only be the user work items including tasks. Some implementations may also include queued
        /// timer and wait callbacks in the count. On Windows, the count is unlikely to include the number of pending IO
        /// completions, as they get posted directly to an IO completion port.
        /// </remarks>
        public static long PendingWorkItemCount
        {
            get
            {
                ThreadPoolWorkQueue workQueue = s_workQueue;
                return ThreadPoolWorkQueue.LocalCount + workQueue.GlobalCount + PendingUnmanagedWorkItemCount;
            }
        }
    }
}

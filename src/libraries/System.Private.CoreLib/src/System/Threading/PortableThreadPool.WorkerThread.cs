// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;

namespace System.Threading
{
    internal sealed partial class PortableThreadPool
    {
        /// <summary>
        /// The worker thread infastructure for the CLR thread pool.
        /// </summary>
        private static partial class WorkerThread
        {
            private static readonly short ThreadsToKeepAlive = DetermineThreadsToKeepAlive();

            private const int SemaphoreSpinCountDefault = 70;

            // This value represents an assumption of how much uncommitted stack space a worker thread may use in the future.
            // Used in calculations to estimate when to throttle the rate of thread injection to reduce the possibility of
            // preexisting threads from running out of memory when using new stack space in low-memory situations.
            public const int EstimatedAdditionalStackUsagePerThreadBytes = 64 << 10; // 64 KB

            private static short DetermineThreadsToKeepAlive()
            {
                const short DefaultThreadsToKeepAlive = 0;

                // The number of worker threads to keep alive after they are created. Set to -1 to keep all created worker
                // threads alive. When the ThreadTimeoutMs config value is also set, for worker threads the timeout applies to
                // worker threads that are in excess of the number configured for ThreadsToKeepAlive.
                short threadsToKeepAlive =
                    AppContextConfigHelper.GetInt16Config(
                        "System.Threading.ThreadPool.ThreadsToKeepAlive",
                        "DOTNET_ThreadPool_ThreadsToKeepAlive",
                        DefaultThreadsToKeepAlive);
                return threadsToKeepAlive >= -1 ? threadsToKeepAlive : DefaultThreadsToKeepAlive;
            }

            /// <summary>
            /// Semaphore for controlling how many threads are currently working.
            /// </summary>
            private static readonly LowLevelLifoSemaphore s_semaphore =
                new LowLevelLifoSemaphore(
                    0,
                    MaxPossibleThreadCount,
                    AppContextConfigHelper.GetInt32ComPlusOrDotNetConfig(
                        "System.Threading.ThreadPool.UnfairSemaphoreSpinLimit",
                        "ThreadPool_UnfairSemaphoreSpinLimit",
                        SemaphoreSpinCountDefault,
                        false),
                    onWait: () =>
                    {
                        if (NativeRuntimeEventSource.Log.IsEnabled())
                        {
                            NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadWait(
                                (uint)ThreadPoolInstance._separated.counts.VolatileRead().NumExistingThreads);
                        }
                    });

            private static readonly ThreadStart s_workerThreadStart = WorkerThreadStart;

            private static void CreateWorkerThread()
            {
                // Thread pool threads must start in the default execution context without transferring the context, so
                // using UnsafeStart() instead of Start()
                Thread workerThread = new Thread(s_workerThreadStart);
                workerThread.IsThreadPoolThread = true;
                workerThread.IsBackground = true;
                workerThread.SetThreadPoolWorkerThreadName();
                workerThread.UnsafeStart();
            }

            private static void WorkerThreadStart()
            {
                PortableThreadPool threadPoolInstance = ThreadPoolInstance;

                if (NativeRuntimeEventSource.Log.IsEnabled())
                {
                    NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadStart(
                        (uint)threadPoolInstance._separated.counts.VolatileRead().NumExistingThreads);
                }

                LowLevelLock threadAdjustmentLock = threadPoolInstance._threadAdjustmentLock;
                LowLevelLifoSemaphore semaphore = s_semaphore;

                // Determine the idle timeout to use for this thread. Some threads may always be kept alive based on config.
                int timeoutMs = ThreadPoolThreadTimeoutMs;
                if (ThreadsToKeepAlive != 0)
                {
                    if (ThreadsToKeepAlive < 0)
                    {
                        timeoutMs = Timeout.Infinite;
                    }
                    else
                    {
                        int count = threadPoolInstance._numThreadsBeingKeptAlive;
                        while (count < ThreadsToKeepAlive)
                        {
                            int countBeforeUpdate =
                                Interlocked.CompareExchange(ref threadPoolInstance._numThreadsBeingKeptAlive, count + 1, count);
                            if (countBeforeUpdate == count)
                            {
                                timeoutMs = Timeout.Infinite;
                                break;
                            }

                            count = countBeforeUpdate;
                        }
                    }
                }

                while (true)
                {
                    bool spinWait = true;
                    while (semaphore.Wait(timeoutMs, spinWait))
                    {
                        WorkerDoWork(threadPoolInstance, ref spinWait);
                    }

                    if (ShouldExitWorker(threadPoolInstance, threadAdjustmentLock))
                    {
                        break;
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static void WorkerDoWork(PortableThreadPool threadPoolInstance, ref bool spinWait)
            {
                bool alreadyRemovedWorkingWorker = false;
                while (TakeActiveRequest(threadPoolInstance))
                {
                    threadPoolInstance._separated.lastDequeueTime = Environment.TickCount;
                    if (!ThreadPoolWorkQueue.Dispatch())
                    {
                        // ShouldStopProcessingWorkNow() caused the thread to stop processing work, and it would have
                        // already removed this working worker in the counts. This typically happens when hill climbing
                        // decreases the worker thread count goal.
                        alreadyRemovedWorkingWorker = true;
                        break;
                    }

                    if (threadPoolInstance._separated.numRequestedWorkers <= 0)
                    {
                        break;
                    }

                    // In highly bursty cases with short bursts of work, especially in the portable thread pool
                    // implementation, worker threads are being released and entering Dispatch very quickly, not finding
                    // much work in Dispatch, and soon afterwards going back to Dispatch, causing extra thrashing on
                    // data and some interlocked operations, and similarly when the thread pool runs out of work. Since
                    // there is a pending request for work, introduce a slight delay before serving the next request.
                    // The spin-wait is mainly for when the sleep is not effective due to there being no other threads
                    // to schedule.
                    Thread.UninterruptibleSleep0();
                    if (!Environment.IsSingleProcessor)
                    {
                        Thread.SpinWait(1);
                    }
                }

                // Don't spin-wait on the semaphore next time if the thread was actively stopped from processing work,
                // as it's unlikely that the worker thread count goal would be increased again so soon afterwards that
                // the semaphore would be released within the spin-wait window
                spinWait = !alreadyRemovedWorkingWorker;

                if (!alreadyRemovedWorkingWorker)
                {
                    // If we woke up but couldn't find a request, or ran out of work items to process, we need to update
                    // the number of working workers to reflect that we are done working for now
                    RemoveWorkingWorker(threadPoolInstance);
                }
            }

            // returns true if the worker is shutting down
            // returns false if we should do another iteration
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static bool ShouldExitWorker(PortableThreadPool threadPoolInstance, LowLevelLock threadAdjustmentLock)
            {
                // The thread cannot exit if it has IO pending, otherwise the IO may be canceled
                if (IsIOPending)
                {
                    return false;
                }

                threadAdjustmentLock.Acquire();
                try
                {
                    // At this point, the thread's wait timed out. We are shutting down this thread.
                    // We are going to decrement the number of existing threads to no longer include this one
                    // and then change the max number of threads in the thread pool to reflect that we don't need as many
                    // as we had. Finally, we are going to tell hill climbing that we changed the max number of threads.
                    ThreadCounts counts = threadPoolInstance._separated.counts;
                    while (true)
                    {
                        // Since this thread is currently registered as an existing thread, if more work comes in meanwhile,
                        // this thread would be expected to satisfy the new work. Ensure that NumExistingThreads is not
                        // decreased below NumProcessingWork, as that would be indicative of such a case.
                        if (counts.NumExistingThreads <= counts.NumProcessingWork)
                        {
                            // In this case, enough work came in that this thread should not time out and should go back to work.
                            return false;
                        }

                        ThreadCounts newCounts = counts;
                        short newNumExistingThreads = --newCounts.NumExistingThreads;
                        short newNumThreadsGoal =
                            Math.Max(
                                threadPoolInstance.MinThreadsGoal,
                                Math.Min(newNumExistingThreads, counts.NumThreadsGoal));
                        newCounts.NumThreadsGoal = newNumThreadsGoal;

                        ThreadCounts oldCounts =
                            threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);
                        if (oldCounts == counts)
                        {
                            HillClimbing.ThreadPoolHillClimber.ForceChange(
                                newNumThreadsGoal,
                                HillClimbing.StateOrTransition.ThreadTimedOut);
                            if (NativeRuntimeEventSource.Log.IsEnabled())
                            {
                                NativeRuntimeEventSource.Log.ThreadPoolWorkerThreadStop((uint)newNumExistingThreads);
                            }
                            return true;
                        }

                        counts = oldCounts;
                    }
                }
                finally
                {
                    threadAdjustmentLock.Release();
                }
            }

            /// <summary>
            /// Reduce the number of working workers by one, but maybe add back a worker (possibily this thread) if a thread request comes in while we are marking this thread as not working.
            /// </summary>
            private static void RemoveWorkingWorker(PortableThreadPool threadPoolInstance)
            {
                // A compare-exchange loop is used instead of Interlocked.Decrement or Interlocked.Add to defensively prevent
                // NumProcessingWork from underflowing. See the setter for NumProcessingWork.
                ThreadCounts counts = threadPoolInstance._separated.counts;
                while (true)
                {
                    ThreadCounts newCounts = counts;
                    newCounts.NumProcessingWork--;

                    ThreadCounts countsBeforeUpdate =
                        threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);
                    if (countsBeforeUpdate == counts)
                    {
                        break;
                    }

                    counts = countsBeforeUpdate;
                }

                // It's possible that we decided we had thread requests just before a request came in,
                // but reduced the worker count *after* the request came in.  In this case, we might
                // miss the notification of a thread request.  So we wake up a thread (maybe this one!)
                // if there is work to do.
                if (threadPoolInstance._separated.numRequestedWorkers > 0)
                {
                    MaybeAddWorkingWorker(threadPoolInstance);
                }
            }

            internal static void MaybeAddWorkingWorker(PortableThreadPool threadPoolInstance)
            {
                ThreadCounts counts = threadPoolInstance._separated.counts;
                short numExistingThreads, numProcessingWork, newNumExistingThreads, newNumProcessingWork;
                while (true)
                {
                    numProcessingWork = counts.NumProcessingWork;
                    if (numProcessingWork >= counts.NumThreadsGoal)
                    {
                        return;
                    }

                    newNumProcessingWork = (short)(numProcessingWork + 1);
                    numExistingThreads = counts.NumExistingThreads;
                    newNumExistingThreads = Math.Max(numExistingThreads, newNumProcessingWork);

                    ThreadCounts newCounts = counts;
                    newCounts.NumProcessingWork = newNumProcessingWork;
                    newCounts.NumExistingThreads = newNumExistingThreads;

                    ThreadCounts oldCounts = threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);

                    if (oldCounts == counts)
                    {
                        break;
                    }

                    counts = oldCounts;
                }

                int toCreate = newNumExistingThreads - numExistingThreads;
                int toRelease = newNumProcessingWork - numProcessingWork;

                if (toRelease > 0)
                {
                    s_semaphore.Release(toRelease);
                }

                while (toCreate > 0)
                {
                    CreateWorkerThread();
                    toCreate--;
                }
            }

            /// <summary>
            /// Returns if the current thread should stop processing work on the thread pool.
            /// A thread should stop processing work on the thread pool when work remains only when
            /// there are more worker threads in the thread pool than we currently want.
            /// </summary>
            /// <returns>Whether or not this thread should stop processing work even if there is still work in the queue.</returns>
            internal static bool ShouldStopProcessingWorkNow(PortableThreadPool threadPoolInstance)
            {
                ThreadCounts counts = threadPoolInstance._separated.counts;
                while (true)
                {
                    // When there are more threads processing work than the thread count goal, it may have been decided
                    // to decrease the number of threads. Stop processing if the counts can be updated. We may have more
                    // threads existing than the thread count goal and that is ok, the cold ones will eventually time out if
                    // the thread count goal is not increased again. This logic is a bit different from the original CoreCLR
                    // code from which this implementation was ported, which turns a processing thread into a retired thread
                    // and checks for pending requests like RemoveWorkingWorker. In this implementation there are
                    // no retired threads, so only the count of threads processing work is considered.
                    if (counts.NumProcessingWork <= counts.NumThreadsGoal)
                    {
                        return false;
                    }

                    ThreadCounts newCounts = counts;
                    newCounts.NumProcessingWork--;

                    ThreadCounts oldCounts = threadPoolInstance._separated.counts.InterlockedCompareExchange(newCounts, counts);

                    if (oldCounts == counts)
                    {
                        return true;
                    }
                    counts = oldCounts;
                }
            }

            private static bool TakeActiveRequest(PortableThreadPool threadPoolInstance)
            {
                int count = threadPoolInstance._separated.numRequestedWorkers;
                while (count > 0)
                {
                    int prevCount = Interlocked.CompareExchange(ref threadPoolInstance._separated.numRequestedWorkers, count - 1, count);
                    if (prevCount == count)
                    {
                        return true;
                    }
                    count = prevCount;
                }
                return false;
            }
        }
    }
}

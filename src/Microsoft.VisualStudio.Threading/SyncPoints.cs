#if DEBUG

namespace Microsoft.VisualStudio.Threading
{
    using System.Diagnostics;
    using System.Threading;

    /// <summary>
    /// A debug help in forcing certain timing conditions to be met as part of replaying a possible race condition.
    /// </summary>
    public static class SyncPoints
    {
        /// <summary>
        /// The lock to enter and wait on as part of this timing control.
        /// </summary>
        private static readonly object SyncObject = new object();

        /// <summary>
        /// The current value of a monotonically increasing sequence number.
        /// </summary>
        private static int current;

        /// <summary>
        /// Blocks the caller until it is time to execute the prescribed step.
        /// </summary>
        /// <param name="step">The sequence number that the calling code should be unblocked for. Callers are only unblocked after the previous step has been unblocked.</param>
        /// <param name="doNotBlockBefore">If the sequence number if smaller than this number, do not block.</param>
        public static void Step(int step, int? doNotBlockBefore = null)
        {
            lock (SyncObject)
            {
                if (doNotBlockBefore.HasValue && current < doNotBlockBefore.Value)
                {
#if NET45
                    Debug.WriteLine($"Allowing step {step} through because the current step {current} is less than {doNotBlockBefore}.");
#endif
                    return;
                }

                while (current + 1 < step)
                {
                    Monitor.Wait(SyncObject);
                }

                if (current + 1 == step)
                {
#if NET45
                    Debug.WriteLine($"Allowing step {step} through in sequence.");
#endif
                    current = step;
                    Monitor.PulseAll(SyncObject);
                }
                else
                {
#if NET45
                    Debug.WriteLine($"Allowing step {step} through because its time in the sequence has already passed.");
#endif
                }
            }
        }
    }
}

#endif

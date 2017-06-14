namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class AsyncBarrierTests : TestBase
    {
        public AsyncBarrierTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public void ZeroParticipantsThrow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncBarrier(0));
        }

        [Fact]
        public async Task OneParticipant()
        {
            var barrier = new AsyncBarrier(1);
            await barrier.SignalAndWait();
        }

        [Fact]
        public async Task TwoParticipants()
        {
            await this.MultipleParticipantsHelperAsync(2, 3);
        }

        [Fact]
        public async Task ManyParticipantsAndSteps()
        {
            await this.MultipleParticipantsHelperAsync(100, 50);
        }

        /// <summary>
        /// Verifies that with multiple threads constantly fulfilling the participant count
        /// and resetting and fulfilling it again, it still performs as expected.
        /// </summary>
        [Theory(Skip = "Not passing on AppVeyor consistently. See #119.")]
        [InlineData(2, 1)]
        [InlineData(4, 3)]
        public async Task StressMultipleGroups(int players, int groupSize)
        {
            var barrier = new AsyncBarrier(groupSize);
            var playerTasks = new Task[players];
            int signalsCount = 0;
            using (var cts = new CancellationTokenSource(300))
            {
                for (int i = 0; i < playerTasks.Length; i++)
                {
                    playerTasks[i] = Task.Run(async delegate
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref signalsCount);
                            await barrier.SignalAndWait().WithCancellation(cts.Token).NoThrowAwaitable();
                        }
                    });
                }

                await Task.WhenAll(playerTasks).WithTimeout(UnexpectedTimeout);
            }

            this.Logger.WriteLine("Test reached {0} signals.", signalsCount);
        }

        private async Task MultipleParticipantsHelperAsync(int participants, int steps)
        {
            Requires.Range(participants > 0, nameof(participants));
            Requires.Range(steps > 0, nameof(steps));
            var barrier = new AsyncBarrier(1 + participants); // 1 for test coordinator

            int[] currentStepForActors = new int[participants];
            Task[] actorsFinishedTasks = new Task[participants];
            var actorReady = new AsyncAutoResetEvent();
            for (int i = 0; i < participants; i++)
            {
                int participantIndex = i;
                var progress = new Progress<int>(step =>
                {
                    currentStepForActors[participantIndex] = step;
                    actorReady.Set();
                });
                actorsFinishedTasks[i] = this.ActorAsync(barrier, steps, progress);
            }

            for (int i = 1; i <= steps; i++)
            {
                // Wait until all actors report having completed this step.
                while (!currentStepForActors.All(step => step == i))
                {
                    // Wait for someone to signal a change has been made to the array.
                    await actorReady.WaitAsync();
                }

                // Give the last signal to proceed to the next step.
                await barrier.SignalAndWait();
            }
        }

        private async Task ActorAsync(AsyncBarrier barrier, int steps, IProgress<int> progress)
        {
            Requires.NotNull(barrier, nameof(barrier));
            Requires.Range(steps >= 0, "steps");
            Requires.NotNull(progress, nameof(progress));

            for (int i = 1; i <= steps; i++)
            {
                await Task.Yield();
                progress.Report(i);
                await barrier.SignalAndWait();
            }
        }
    }
}

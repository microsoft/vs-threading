namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using Xunit;

    public class AsyncBarrierTests : TestBase
    {
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

        private async Task MultipleParticipantsHelperAsync(int participants, int steps)
        {
            Requires.Range(participants > 0, "participants");
            Requires.Range(steps > 0, "steps");
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

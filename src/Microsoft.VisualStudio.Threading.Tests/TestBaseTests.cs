namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;
    using Xunit.Sdk;

    public class TestBaseTests : TestBase
    {
        public TestBaseTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

#if DESKTOP || NETCOREAPP2_0
        [Fact]
        public void ExecuteOnSTA_ExecutesDelegateOnSTA()
        {
            bool executed = false;
            this.ExecuteOnSTA(delegate
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.Null(SynchronizationContext.Current);
                executed = true;
            });
            Assert.True(executed);
        }

        [Fact]
        public void ExecuteOnSTA_PropagatesExceptions()
        {
            Assert.Throws<ApplicationException>(() => this.ExecuteOnSTA(() =>
            {
                throw new ApplicationException();
            }));
        }

        [Fact]
        public void ExecuteOnDispatcher_ExecutesDelegateOnSTA()
        {
            bool executed = false;
            this.ExecuteOnDispatcher(delegate
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.NotNull(SynchronizationContext.Current);
                executed = true;
            });
            Assert.True(executed);
        }
#endif

        [Fact]
        public void ExecuteOnDispatcher_PropagatesExceptions()
        {
            Assert.Throws<ApplicationException>(() => this.ExecuteOnDispatcher(() =>
            {
                throw new ApplicationException();
            }));
        }

        [SkippableFact]
        public async Task ExecuteInIsolation_PassingTest()
        {
            if (await this.ExecuteInIsolationAsync())
            {
                this.Logger.WriteLine("Some output from isolated process.");
            }
        }

        [SkippableFact]
        public async Task ExecuteInIsolation_FailingTest()
        {
            bool executeHere;
            try
            {
                executeHere = await this.ExecuteInIsolationAsync();
            }
            catch (XunitException ex)
            {
                // This is the outer invocation and it failed as expected.
                this.Logger.WriteLine(ex.ToString());
                return;
            }

            Assumes.True(executeHere);
            throw new Exception("Intentional test failure");
        }

#if DESKTOP
        [StaFact]
        public async Task ExecuteInIsolation_PassingOnSTA()
        {
            if (await this.ExecuteInIsolationAsync())
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
            }
        }

        [StaFact]
        public async Task ExecuteInIsolation_FailingOnSTA()
        {
            bool executeHere;
            try
            {
                executeHere = await this.ExecuteInIsolationAsync();
            }
            catch (XunitException ex)
            {
                // This is the outer invocation and it failed as expected.
                this.Logger.WriteLine(ex.ToString());
                return;
            }

            Assumes.True(executeHere);
            throw new Exception("Intentional test failure");
        }
#endif
    }
}

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

    public class TestBaseTests : TestBase
    {
        public TestBaseTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

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

        [Fact]
        public void ExecuteOnDispatcher_PropagatesExceptions()
        {
            Assert.Throws<ApplicationException>(() => this.ExecuteOnDispatcher(() =>
            {
                throw new ApplicationException();
            }));
        }
    }
}

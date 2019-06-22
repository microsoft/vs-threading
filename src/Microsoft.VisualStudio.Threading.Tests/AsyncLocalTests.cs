namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;
    using Xunit.Abstractions;

    public class AsyncLocalTests : TestBase
    {
        // Be VERY explicit about the type we're binding against since
        // there is now one in System.Threading and we want to be sure
        // we're testing OUR stuff not THEIRS.
        private Microsoft.VisualStudio.Threading.AsyncLocal<GenericParameterHelper> asyncLocal;

        public AsyncLocalTests(ITestOutputHelper logger)
            : base(logger)
        {
            this.asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<GenericParameterHelper>();
        }

        [Fact]
        public void SetGetNoYield()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;
            Assert.Same(value, this.asyncLocal.Value);
            this.asyncLocal.Value = null;
            Assert.Null(this.asyncLocal.Value);
        }

        [Fact]
        public async Task SetGetWithYield()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;
            await Task.Yield();
            Assert.Same(value, this.asyncLocal.Value);
            this.asyncLocal.Value = null;
            Assert.Null(this.asyncLocal.Value);
        }

        [Fact]
        public async Task ForkedContext()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;
            await Task.WhenAll(
                Task.Run(delegate
                {
                    Assert.Same(value, this.asyncLocal.Value);
                    this.asyncLocal.Value = null;
                    Assert.Null(this.asyncLocal.Value);
                }),
                Task.Run(delegate
                {
                    Assert.Same(value, this.asyncLocal.Value);
                    this.asyncLocal.Value = null;
                    Assert.Null(this.asyncLocal.Value);
                }));

            Assert.Same(value, this.asyncLocal.Value);
            this.asyncLocal.Value = null;
            Assert.Null(this.asyncLocal.Value);
        }

        [Fact]
        public async Task IndependentValuesBetweenContexts()
        {
            await IndependentValuesBetweenContextsHelper<GenericParameterHelper>();
            await IndependentValuesBetweenContextsHelper<object>();
        }

        [Fact]
        public void SetNewValuesRepeatedly()
        {
            for (int i = 0; i < 10; i++)
            {
                var value = new GenericParameterHelper();
                this.asyncLocal.Value = value;
                Assert.Same(value, this.asyncLocal.Value);
            }

            this.asyncLocal.Value = null;
            Assert.Null(this.asyncLocal.Value);
        }

        [Fact]
        public void SetSameValuesRepeatedly()
        {
            var value = new GenericParameterHelper();
            for (int i = 0; i < 10; i++)
            {
                this.asyncLocal.Value = value;
                Assert.Same(value, this.asyncLocal.Value);
            }

            this.asyncLocal.Value = null;
            Assert.Null(this.asyncLocal.Value);
        }

        [Fact, Trait("GC", "true")]
        public void SurvivesGC()
        {
            var value = new GenericParameterHelper(5);
            this.asyncLocal.Value = value;
            Assert.Same(value, this.asyncLocal.Value);

            GC.Collect();
            Assert.Same(value, this.asyncLocal.Value);

            value = null;
            GC.Collect();
            Assert.Equal(5, this.asyncLocal.Value.Data);
        }

        [Fact]
        public void NotDisruptedByTestContextWriteLine()
        {
            var value = new GenericParameterHelper();
            this.asyncLocal.Value = value;

            // TestContext.WriteLine causes the CallContext to be serialized.
            // When a .testsettings file is applied to the test runner, the
            // original contents of the CallContext are replaced with a
            // serialize->deserialize clone, which can break the reference equality
            // of the objects stored in the AsyncLocal class's private fields
            // if it's not done properly.
            this.Logger.WriteLine("Foobar");

            Assert.NotNull(this.asyncLocal.Value);
            Assert.Same(value, this.asyncLocal.Value);
        }

        [Fact]
        public void ValuePersistsAcrossExecutionContextChanges()
        {
            var jtLocal = new AsyncLocal<object>();
            jtLocal.Value = 1;
            Func<Task> asyncMethod = async delegate
            {
                SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
                Assert.Equal(1, jtLocal.Value);
                jtLocal.Value = 3;
                Assert.Equal(3, jtLocal.Value);
                await TaskScheduler.Default;
                Assert.Equal(3, jtLocal.Value);
            };
            asyncMethod().GetAwaiter().GetResult();

            Assert.Equal(1, jtLocal.Value);
        }

        [Fact, Trait("Performance", "true")]
        public void AsyncLocalPerfTest()
        {
            var values = Enumerable.Range(1, 50000).Select(n => new GenericParameterHelper(n)).ToArray();

            var creates = Stopwatch.StartNew();
            for (int i = 0; i < values.Length; i++)
            {
                this.asyncLocal = new Microsoft.VisualStudio.Threading.AsyncLocal<GenericParameterHelper>();
            }
            creates.Stop();

            var writes = Stopwatch.StartNew();
            for (int i = 0; i < values.Length; i++)
            {
                this.asyncLocal.Value = values[0];
            }

            writes.Stop();

            var reads = Stopwatch.StartNew();
            for (int i = 0; i < values.Length; i++)
            {
                var value = this.asyncLocal.Value;
            }

            reads.Stop();

            // We don't actually validate the perf here. We just print out the results.
            this.Logger.WriteLine("Creating {0} instances took {1} ms", values.Length, creates.ElapsedMilliseconds);
            this.Logger.WriteLine("Saving {0} values took {1} ms", values.Length, writes.ElapsedMilliseconds);
            this.Logger.WriteLine("Reading {0} values took {1} ms", values.Length, reads.ElapsedMilliseconds);
        }

#if DESKTOP
        [Fact]
        public void CallAcrossAppDomainBoundariesWithNonSerializableData()
        {
            var otherDomain = AppDomain.CreateDomain("test domain", AppDomain.CurrentDomain.Evidence, AppDomain.CurrentDomain.SetupInformation);
            try
            {
                var proxy = (OtherDomainProxy)otherDomain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(OtherDomainProxy).FullName);

                // Verify we can call it first.
                proxy.SomeMethod(AppDomain.CurrentDomain.Id);

                // Verify we can call it while AsyncLocal has a non-serializable value.
                var value = new GenericParameterHelper();
                this.asyncLocal.Value = value;
                proxy.SomeMethod(AppDomain.CurrentDomain.Id);
                Assert.Same(value, this.asyncLocal.Value);

                // Nothing permanently damaged in the ability to set/get values.
                this.asyncLocal.Value = null;
                this.asyncLocal.Value = value;
                Assert.Same(value, this.asyncLocal.Value);

                // Verify we can call it after clearing the value.
                this.asyncLocal.Value = null;
                proxy.SomeMethod(AppDomain.CurrentDomain.Id);
            }
            finally
            {
                AppDomain.Unload(otherDomain);
            }
        }
#endif

        private static async Task IndependentValuesBetweenContextsHelper<T>()
            where T : class, new()
        {
            var asyncLocal = new AsyncLocal<T>();
            var player1 = new AsyncAutoResetEvent();
            var player2 = new AsyncAutoResetEvent();
            await Task.WhenAll(
                Task.Run(async delegate
                {
                    Assert.Null(asyncLocal.Value);
                    var value = new T();
                    asyncLocal.Value = value;
                    Assert.Same(value, asyncLocal.Value);
                    player1.Set();
                    await player2.WaitAsync();
                    Assert.Same(value, asyncLocal.Value);
                }),
                Task.Run(async delegate
                {
                    await player1.WaitAsync();
                    Assert.Null(asyncLocal.Value);
                    var value = new T();
                    asyncLocal.Value = value;
                    Assert.Same(value, asyncLocal.Value);
                    asyncLocal.Value = null;
                    player2.Set();
                }));

            Assert.Null(asyncLocal.Value);
        }

#if DESKTOP
        private class OtherDomainProxy : MarshalByRefObject
        {
            internal void SomeMethod(int callingAppDomainId)
            {
                Assert.NotEqual(callingAppDomainId, AppDomain.CurrentDomain.Id); // AppDomain boundaries not crossed.
            }
        }
#endif
    }
}

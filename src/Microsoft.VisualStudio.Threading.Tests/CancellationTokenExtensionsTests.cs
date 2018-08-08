namespace Microsoft.VisualStudio.Threading.Tests
{
    using System;
    using System.Threading;
    using Xunit;
    using Xunit.Abstractions;

    public class CancellationTokenExtensionsTests : TestBase
    {
        public CancellationTokenExtensionsTests(ITestOutputHelper logger)
            : base(logger)
        {
        }

        [Fact]
        public void CombineWith_NoneCancelable()
        {
            using (var combined = CancellationToken.None.CombineWith(CancellationToken.None))
            {
                Assert.False(combined.Token.CanBeCanceled);
            }
        }

        [Fact]
        public void CombineWith_FirstCancelable()
        {
            var cts = new CancellationTokenSource();
            using (var combined = cts.Token.CombineWith(CancellationToken.None))
            {
                Assert.True(combined.Token.CanBeCanceled);
                Assert.Equal(cts.Token, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_SecondCancelable()
        {
            var cts = new CancellationTokenSource();
            using (var combined = CancellationToken.None.CombineWith(cts.Token))
            {
                Assert.True(combined.Token.CanBeCanceled);
                Assert.Equal(cts.Token, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_BothCancelable()
        {
            var cts1 = new CancellationTokenSource();
            var cts2 = new CancellationTokenSource();
            using (var combined = cts1.Token.CombineWith(cts2.Token))
            {
                Assert.True(combined.Token.CanBeCanceled);
                Assert.NotEqual(cts1.Token, combined.Token);
                Assert.NotEqual(cts2.Token, combined.Token);

                cts1.Cancel();
                Assert.True(combined.Token.IsCancellationRequested);
            }
        }

        [Fact]
        public void CombineWith_BothCancelable_FirstAlreadyCanceled()
        {
            var first = new CancellationToken(true);
            var cts2 = new CancellationTokenSource();
            using (var combined = first.CombineWith(cts2.Token))
            {
                Assert.Equal(first, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_BothCancelable_SecondAlreadyCanceled()
        {
            var cts1 = new CancellationTokenSource();
            var second = new CancellationToken(true);
            using (var combined = cts1.Token.CombineWith(second))
            {
                Assert.Equal(second, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_Array_Empty()
        {
            var cts1 = new CancellationTokenSource();
            using (var combined = cts1.Token.CombineWith())
            {
                Assert.Equal(cts1.Token, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_Array_Null()
        {
            var cts1 = new CancellationTokenSource();
            Assert.Throws<ArgumentNullException>(() => cts1.Token.CombineWith(null));
        }

        [Fact]
        public void CombineWith_Array_Empty_OriginalNonCancelable()
        {
            using (var combined = CancellationToken.None.CombineWith())
            {
                Assert.False(combined.Token.CanBeCanceled);
            }
        }

        [Fact]
        public void CombineWith_Array_Empty_OriginalAlreadyCanceled()
        {
            CancellationToken cancellationToken = new CancellationToken(true);
            using (var combined = cancellationToken.CombineWith())
            {
                Assert.True(combined.Token.IsCancellationRequested);
                Assert.Equal(cancellationToken, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_Array_OneArrayElementCancelable_First()
        {
            var cts1 = new CancellationTokenSource();
            using (var combined = CancellationToken.None.CombineWith(cts1.Token, CancellationToken.None))
            {
                Assert.Equal(cts1.Token, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_Array_OneArrayElementCancelable_Second()
        {
            var cts1 = new CancellationTokenSource();
            using (var combined = CancellationToken.None.CombineWith(cts1.Token, CancellationToken.None))
            {
                Assert.Equal(cts1.Token, combined.Token);
            }
        }

        [Fact]
        public void CombineWith_Array_OneArrayElementPreCanceled()
        {
            var ct = new CancellationToken(true);
            using (var combined = CancellationToken.None.CombineWith(CancellationToken.None, ct, CancellationToken.None))
            {
                Assert.Equal(ct, combined.Token);
                Assert.True(combined.Token.IsCancellationRequested);
            }
        }

        [Fact]
        public void CombineWith_Array_TwoArrayElementsCancelable()
        {
            var cts1 = new CancellationTokenSource();
            var cts2 = new CancellationTokenSource();
            using (var combined = CancellationToken.None.CombineWith(cts1.Token, cts2.Token))
            {
                Assert.True(combined.Token.CanBeCanceled);
                Assert.NotEqual(cts1.Token, combined.Token);
                Assert.NotEqual(cts2.Token, combined.Token);
                cts1.Cancel();
                Assert.True(combined.Token.IsCancellationRequested);
            }
        }

        [Fact]
        public void CombineWith_Array_TwoCancelable()
        {
            var cts1 = new CancellationTokenSource();
            var cts2 = new CancellationTokenSource();
            using (var combined = cts1.Token.CombineWith(cts2.Token, CancellationToken.None))
            {
                Assert.NotEqual(cts1.Token, combined.Token);
                Assert.NotEqual(cts2.Token, combined.Token);
                cts2.Cancel();
                Assert.True(combined.Token.IsCancellationRequested);
            }
        }

        [Theory]
        [CombinatorialData]
        public void CombineWith_Array_TwoCancelable_AmidMany_FirstOriginal(bool cancelFirst)
        {
            var cts1 = new CancellationTokenSource();
            var cts2 = new CancellationTokenSource();
            using (var combined = cts1.Token.CombineWith(CancellationToken.None, cts2.Token, CancellationToken.None))
            {
                Assert.NotEqual(cts1.Token, combined.Token);
                Assert.NotEqual(cts2.Token, combined.Token);
                if (cancelFirst)
                {
                    cts1.Cancel();
                }
                else
                {
                    cts2.Cancel();
                }

                Assert.True(combined.Token.IsCancellationRequested);
            }
        }

        [Theory]
        [CombinatorialData]
        public void CombineWith_Array_TwoCancelable_AmidMany(bool cancelFirst)
        {
            var cts1 = new CancellationTokenSource();
            var cts2 = new CancellationTokenSource();
            using (var combined = CancellationToken.None.CombineWith(cts1.Token, CancellationToken.None, cts2.Token, CancellationToken.None))
            {
                Assert.NotEqual(cts1.Token, combined.Token);
                Assert.NotEqual(cts2.Token, combined.Token);
                if (cancelFirst)
                {
                    cts1.Cancel();
                }
                else
                {
                    cts2.Cancel();
                }

                Assert.True(combined.Token.IsCancellationRequested);
            }
        }

        [Fact]
        public void CombineWith_Array_ThreeCancelable_AmidMany()
        {
            var cts1 = new CancellationTokenSource();
            var cts2 = new CancellationTokenSource();
            var cts3 = new CancellationTokenSource();
            using (var combined = CancellationToken.None.CombineWith(cts1.Token, CancellationToken.None, cts2.Token, CancellationToken.None, cts3.Token))
            {
                Assert.NotEqual(cts1.Token, combined.Token);
                Assert.NotEqual(cts2.Token, combined.Token);
                Assert.NotEqual(cts3.Token, combined.Token);

                cts2.Cancel();
                Assert.True(combined.Token.IsCancellationRequested);
            }
        }

        [Fact]
        public void CombinedCancellationToken_Equality_BetweenEqualInstances_None()
        {
            var combined1 = CancellationToken.None.CombineWith(CancellationToken.None);
            var combined2 = CancellationToken.None.CombineWith(CancellationToken.None);
            Assert.Equal(combined1.GetHashCode(), combined2.GetHashCode());
            Assert.True(combined1.Equals(combined2));
            Assert.True(combined1 == combined2);
            Assert.False(combined1 != combined2);
        }

        [Fact]
        public void CombinedCancellationToken_Equality_WithRealToken()
        {
            var cts = new CancellationTokenSource();
            var combined1 = cts.Token.CombineWith(CancellationToken.None);
            var combined2 = cts.Token.CombineWith(CancellationToken.None);
            Assert.Equal(combined1.GetHashCode(), combined2.GetHashCode());
            Assert.True(combined1.Equals(combined2));
            Assert.True(combined1 == combined2);
            Assert.False(combined1 != combined2);
        }

        [Fact]
        public void CombinedCancellationToken_Inequality_WithRealToken()
        {
            var cts = new CancellationTokenSource();
            var combined1 = cts.Token.CombineWith(CancellationToken.None);
            var combined2 = CancellationToken.None.CombineWith(CancellationToken.None);
            Assert.NotEqual(combined1.GetHashCode(), combined2.GetHashCode());
            Assert.False(combined1.Equals(combined2));
            Assert.False(combined1 == combined2);
            Assert.True(combined1 != combined2);
        }
    }
}

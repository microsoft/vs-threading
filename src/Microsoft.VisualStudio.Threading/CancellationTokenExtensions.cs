// Copyright (c) PlaceholderCompany. All rights reserved.

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Threading;

    /// <summary>
    /// Extensions to <see cref="CancellationToken"/>.
    /// </summary>
    public static class CancellationTokenExtensions
    {
        /// <summary>
        /// Creates a new <see cref="CancellationToken"/> that is canceled when any of a set of other tokens are canceled.
        /// </summary>
        /// <param name="original">The first token.</param>
        /// <param name="other">The second token.</param>
        /// <returns>A struct that contains the combined <see cref="CancellationToken"/> and a means to release memory when you're done using it.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000", Justification = "The CancellationTokenSource is created and returned in a struct responsible for its disposal.")]
        public static CombinedCancellationToken CombineWith(this CancellationToken original, CancellationToken other)
        {
            if (original.IsCancellationRequested || !other.CanBeCanceled)
            {
                return new CombinedCancellationToken(original);
            }

            if (other.IsCancellationRequested || !original.CanBeCanceled)
            {
                return new CombinedCancellationToken(other);
            }

            // This is the most expensive path to take since it involves allocating memory and requiring disposal.
            // Before this point we've checked every condition that would allow us to avoid it.
            return new CombinedCancellationToken(CancellationTokenSource.CreateLinkedTokenSource(original, other));
        }

        /// <summary>
        /// Creates a new <see cref="CancellationToken"/> that is canceled when any of a set of other tokens are canceled.
        /// </summary>
        /// <param name="original">The first token.</param>
        /// <param name="others">The additional tokens.</param>
        /// <returns>A struct that contains the combined <see cref="CancellationToken"/> and a means to release memory when you're done using it.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Reliability", "CA2000", Justification = "The CancellationTokenSource is created and returned in a struct responsible for its disposal.")]
        public static CombinedCancellationToken CombineWith(this CancellationToken original, params CancellationToken[] others)
        {
            Requires.NotNull(others, nameof(others));

            if (original.IsCancellationRequested)
            {
                return new CombinedCancellationToken(original);
            }

            int cancelableTokensCount = original.CanBeCanceled ? 1 : 0;
            foreach (var other in others)
            {
                if (other.IsCancellationRequested)
                {
                    return new CombinedCancellationToken(other);
                }

                if (other.CanBeCanceled)
                {
                    cancelableTokensCount++;
                }
            }

            switch (cancelableTokensCount)
            {
                case 0:
                    return new CombinedCancellationToken(CancellationToken.None);
                case 1:
                    if (original.CanBeCanceled)
                    {
                        return new CombinedCancellationToken(original);
                    }

                    foreach (var other in others)
                    {
                        if (other.CanBeCanceled)
                        {
                            return new CombinedCancellationToken(other);
                        }
                    }

                    throw Assumes.NotReachable();
                case 2:
                    CancellationToken first = CancellationToken.None;
                    CancellationToken second = CancellationToken.None;

                    if (original.CanBeCanceled)
                    {
                        first = original;
                    }

                    foreach (var other in others)
                    {
                        if (other.CanBeCanceled)
                        {
                            if (first.CanBeCanceled)
                            {
                                second = other;
                            }
                            else
                            {
                                first = other;
                            }
                        }
                    }

                    Assumes.True(first.CanBeCanceled && second.CanBeCanceled);

                    // Call the overload that takes two CancellationTokens explicitly to avoid an array allocation.
                    return new CombinedCancellationToken(CancellationTokenSource.CreateLinkedTokenSource(first, second));
                default:
                    // This is the most expensive path to take since it involves allocating memory and requiring disposal.
                    // Before this point we've checked every condition that would allow us to avoid it.
                    var cancelableTokens = new CancellationToken[cancelableTokensCount];
                    int i = 0;
                    foreach (var other in others)
                    {
                        if (other.CanBeCanceled)
                        {
                            cancelableTokens[i++] = other;
                        }
                    }

                    return new CombinedCancellationToken(CancellationTokenSource.CreateLinkedTokenSource(cancelableTokens));
            }
        }

        /// <summary>
        /// Provides access to a <see cref="System.Threading.CancellationToken"/> that combines multiple other tokens,
        /// and allows convenient disposal of any applicable <see cref="CancellationTokenSource"/>.
        /// </summary>
        public readonly struct CombinedCancellationToken : IDisposable, IEquatable<CombinedCancellationToken>
        {
            /// <summary>
            /// The object to dispose when this struct is disposed.
            /// </summary>
            private readonly CancellationTokenSource cts;

            /// <summary>
            /// Initializes a new instance of the <see cref="CombinedCancellationToken"/> struct
            /// that contains an aggregate <see cref="System.Threading.CancellationToken"/> whose source must be disposed.
            /// </summary>
            /// <param name="cancellationTokenSource">The cancellation token source.</param>
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1062", Justification = "We are prepared to handle null values for CancellationTokenSource.")]
            public CombinedCancellationToken(CancellationTokenSource cancellationTokenSource)
            {
                this.cts = cancellationTokenSource;
                this.Token = cancellationTokenSource.Token;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="CombinedCancellationToken"/> struct
            /// that represents just a single, non-disposable <see cref="System.Threading.CancellationToken"/>.
            /// </summary>
            /// <param name="cancellationToken">The cancellation token</param>
            public CombinedCancellationToken(CancellationToken cancellationToken)
            {
                this.cts = null;
                this.Token = cancellationToken;
            }

            /// <summary>
            /// Checks whether two instances of <see cref="CombinedCancellationToken"/> are equal.
            /// </summary>
            /// <param name="left">The left operand.</param>
            /// <param name="right">The right operand.</param>
            /// <returns><c>true</c> if they are equal; <c>false</c> otherwise.</returns>
            public static bool operator ==(CombinedCancellationToken left, CombinedCancellationToken right) => left.Equals(right);

            /// <summary>
            /// Checks whether two instances of <see cref="CombinedCancellationToken"/> are not equal.
            /// </summary>
            /// <param name="left">The left operand.</param>
            /// <param name="right">The right operand.</param>
            /// <returns><c>true</c> if they are not equal; <c>false</c> if they are equal.</returns>
            public static bool operator !=(CombinedCancellationToken left, CombinedCancellationToken right) => !(left == right);

            /// <summary>
            /// Gets the combined cancellation token.
            /// </summary>
            public CancellationToken Token { get; }

            /// <summary>
            /// Disposes the <see cref="CancellationTokenSource"/> behind this combined token, if any.
            /// </summary>
            public void Dispose()
            {
                this.cts?.Dispose();
            }

            /// <inheritdoc />
            public override bool Equals(object obj) => obj is CombinedCancellationToken other && this.Equals(other);

            /// <inheritdoc />
            public bool Equals(CombinedCancellationToken other) => this.cts == other.cts && this.Token.Equals(other.Token);

            /// <inheritdoc />
            public override int GetHashCode() => (this.cts?.GetHashCode() ?? 0) + this.Token.GetHashCode();
        }
    }
}

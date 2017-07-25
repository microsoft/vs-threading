/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Light-up functionality that requires a generic type argument.
    /// </summary>
    /// <typeparam name="T">The generic type argument.</typeparam>
    internal static class LightUps<T>
    {
#if !TRYSETCANCELEDCT
        /// <summary>
        /// A delegate that invokes the <see cref="TaskCompletionSource{TResult}.TrySetCanceled"/>
        /// method that takes <see cref="CancellationToken"/> as an argument.
        /// Will be <c>null</c> on .NET Framework versions under 4.6.
        /// </summary>
        internal static readonly Func<TaskCompletionSource<T>, CancellationToken, bool> TrySetCanceled;
#endif

#if !ASYNCLOCAL

        /// <summary>
        /// A value indicating whether the BCL AsyncLocal{T} type is available.
        /// </summary>
        internal static readonly bool IsAsyncLocalSupported;

        /// <summary>
        /// The System.Threading.AsyncLocal{T} closed generic type, if present.
        /// </summary>
        /// <remarks>
        /// When running on .NET 4.6, it will be present.
        /// This field will be <c>null</c> on earlier versions of .NET.
        /// </remarks>
        private static readonly Type BclAsyncLocalType;

        /// <summary>
        /// The AsyncLocal{T}.Value PropertyInfo.
        /// </summary>
        private static readonly PropertyInfo BclAsyncLocalValueProperty;

#endif

        /// <summary>
        /// Initializes static members of the <see cref="LightUps{T}"/> class.
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline", Justification = "These fields have dependency relationships.")]
        static LightUps()
        {
            if (!LightUps.ForceNet45Mode)
            {
#if !TRYSETCANCELEDCT
                var methodInfo = typeof(TaskCompletionSource<T>).GetTypeInfo()
                    .GetDeclaredMethods(nameof(TaskCompletionSource<int>.TrySetCanceled))
                    .FirstOrDefault(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(CancellationToken));
                if (methodInfo != null)
                {
                    TrySetCanceled = (Func<TaskCompletionSource<T>, CancellationToken, bool>)methodInfo.CreateDelegate(typeof(Func<TaskCompletionSource<T>, CancellationToken, bool>));
                }
#endif

#if !ASYNCLOCAL
                if (LightUps.BclAsyncLocalType != null)
                {
                    BclAsyncLocalType = LightUps.BclAsyncLocalType.MakeGenericType(typeof(T));
                    BclAsyncLocalValueProperty = BclAsyncLocalType.GetTypeInfo().GetDeclaredProperty("Value");
                    IsAsyncLocalSupported = true;
                }
#endif
            }
        }

#if !ASYNCLOCAL

        /// <summary>
        /// Creates an instance of the BCL AsyncLocal{T} type.
        /// </summary>
        /// <returns>The constructed instance of AsyncLocal{T}.</returns>
        internal static object CreateAsyncLocal()
        {
            Assumes.True(IsAsyncLocalSupported);
            return AsyncLocalHelper.Instance.CreateAsyncLocal();
        }

        /// <summary>
        /// Sets the value on an AsyncLocal{T} object.
        /// </summary>
        /// <param name="instance">The AsyncLocal{T} instance to change.</param>
        /// <param name="value">The new value to assign.</param>
        internal static void SetAsyncLocalValue(object instance, T value) => AsyncLocalHelper.Instance.Setter(instance, value);

        /// <summary>
        /// Gets the value from an AsyncLocal{T} object.
        /// </summary>
        /// <param name="instance">The instance to read the value from.</param>
        /// <returns>The value.</returns>
        internal static T GetAsyncLocalValue(object instance) => AsyncLocalHelper.Instance.Getter(instance);

        /// <summary>
        /// A non-generic helper that allows creation of and access to AsyncLocal{T}.
        /// </summary>
        private abstract class AsyncLocalHelper
        {
            /// <summary>
            /// The singleton for the type T of the outer class.
            /// </summary>
            internal static readonly AsyncLocalHelper Instance = CreateNew();

            /// <summary>
            /// Gets the AsyncLocal{T}.Value getter.
            /// </summary>
            internal abstract Func<object, T> Getter { get; }

            /// <summary>
            /// Gets the AsyncLocal{T}.Value setter.
            /// </summary>
            internal abstract Action<object, T> Setter { get; }

            /// <summary>
            /// Creates a new instance of AsyncLocal{T}.
            /// </summary>
            /// <returns>The newly created instance.</returns>
            internal abstract object CreateAsyncLocal();

            /// <summary>
            /// Creates the singleton instance of this class.
            /// </summary>
            /// <returns>The instance of this abstract class.</returns>
            private static AsyncLocalHelper CreateNew()
            {
                var genericHelperType = typeof(AsyncLocalHelper<>);
                var instanceHelperType = genericHelperType.MakeGenericType(typeof(T), BclAsyncLocalType);
                return (AsyncLocalHelper)Activator.CreateInstance(instanceHelperType);
            }
        }

        /// <summary>
        /// A generic derived type of <see cref="AsyncLocalHelper"/>
        /// that binds directly to AsyncLocal{T} and fulfills the non-generic
        /// interface defined by its abstract base class.
        /// </summary>
        /// <typeparam name="TAsyncLocal">The closed generic type for AsyncLocal{T} itself.</typeparam>
        private class AsyncLocalHelper<TAsyncLocal> : AsyncLocalHelper
            where TAsyncLocal : new()
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="AsyncLocalHelper{TAsyncLocal}"/> class.
            /// </summary>
            public AsyncLocalHelper()
            {
                var getter = (Func<TAsyncLocal, T>)BclAsyncLocalValueProperty.GetMethod.CreateDelegate(typeof(Func<TAsyncLocal, T>));
                this.Getter = o => getter((TAsyncLocal)o);

                var setter = (Action<TAsyncLocal, T>)BclAsyncLocalValueProperty.SetMethod.CreateDelegate(typeof(Action<TAsyncLocal, T>));
                this.Setter = (o, v) => setter((TAsyncLocal)o, v);
            }

            /// <inheritdoc />
            internal override Func<object, T> Getter { get; }

            /// <inheritdoc />
            internal override Action<object, T> Setter { get; }

            /// <inheritdoc />
            internal override object CreateAsyncLocal() => new TAsyncLocal();
        }
#endif
    }
}

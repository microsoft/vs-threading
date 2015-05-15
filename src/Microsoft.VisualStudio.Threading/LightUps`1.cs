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
        /// <summary>
        /// A delegate that invokes the <see cref="TaskCompletionSource{TResult}.TrySetCanceled"/>
        /// method that takes <see cref="CancellationToken"/> as an argument.
        /// Will be <c>null</c> on .NET Framework versions under 4.6.
        /// </summary>
        internal static readonly Func<TaskCompletionSource<T>, CancellationToken, bool> TrySetCanceled;

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
        /// The default constructor for the System.Threading.AsyncLocal{T} type, if present.
        /// </summary>
        private static readonly ConstructorInfo BclAsyncLocalCtor;

        /// <summary>
        /// The AsyncLocal{T}.Value PropertyInfo.
        /// </summary>
        private static PropertyInfo bclAsyncLocalValueProperty;

        /// <summary>
        /// Initializes static members of the <see cref="LightUps{T}"/> class.
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline", Justification = "These fields have dependency relationships.")]
        static LightUps()
        {
            if (!LightUps.ForceNet45Mode)
            {
                var methodInfo = typeof(TaskCompletionSource<T>).GetTypeInfo()
                    .GetDeclaredMethods(nameof(TaskCompletionSource<int>.TrySetCanceled))
                    .FirstOrDefault(m => m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(CancellationToken));
                if (methodInfo != null)
                {
                    TrySetCanceled = (Func<TaskCompletionSource<T>, CancellationToken, bool>)methodInfo.CreateDelegate(typeof(Func<TaskCompletionSource<T>, CancellationToken, bool>));
                }

                if (LightUps.BclAsyncLocalType != null)
                {
                    BclAsyncLocalType = LightUps.BclAsyncLocalType.MakeGenericType(typeof(T));
                    BclAsyncLocalCtor = BclAsyncLocalType.GetTypeInfo().DeclaredConstructors.FirstOrDefault(ctor => ctor.GetParameters().Length == 0);
                    bclAsyncLocalValueProperty = BclAsyncLocalType.GetTypeInfo().GetDeclaredProperty("Value");
                    IsAsyncLocalSupported = true;
                }
            }
        }

        /// <summary>
        /// Creates an instance of the BCL AsyncLocal{T} type.
        /// </summary>
        /// <param name="getter">The delegate used to retrieve the Value property.</param>
        /// <param name="setter">The delegate used to set the Value property.</param>
        /// <returns>The constructed instance of AsyncLocal{T}.</returns>
        internal static object CreateAsyncLocal(out Func<T> getter, out Action<T> setter)
        {
            Assumes.True(IsAsyncLocalSupported);
            var instance = BclAsyncLocalCtor.Invoke(null);
            getter = (Func<T>)bclAsyncLocalValueProperty.GetMethod.CreateDelegate(typeof(Func<T>), instance);
            setter = (Action<T>)bclAsyncLocalValueProperty.SetMethod.CreateDelegate(typeof(Action<T>), instance);
            return instance;
        }
    }
}

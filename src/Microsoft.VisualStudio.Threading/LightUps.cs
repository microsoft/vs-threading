/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics.CodeAnalysis;
	using System.Linq;
	using System.Reflection;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// A non-generic class used to store statics that do not vary by generic type argument.
	/// </summary>
	internal static class LightUps {
		/// <summary>
		/// The System.Threading.AsyncLocal open generic type, if present.
		/// </summary>
		/// <remarks>
		/// When running on .NET 4.6, it will be present. 
		/// This field will be <c>null</c> on earlier versions of .NET.
		/// </remarks>
		internal static readonly Type BclAsyncLocalType = Type.GetType("System.Threading.AsyncLocal`1");

		/// <summary>
		/// A shareable empty array of Type.
		/// </summary>
		internal static readonly Type[] EmptyTypeArray = new Type[0];
	}

	/// <summary>
	/// Light-up functionality that requires a generic type argument.
	/// </summary>
	/// <typeparam name="T">The generic type argument.</typeparam>
	internal static class LightUps<T> {
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
		private static PropertyInfo BclAsyncLocalValueProperty;

		/// <summary>
		/// Initializes static members of the <see cref="LightUps{T}"/> class.
		/// </summary>
		[SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline", Justification = "These fields have dependency relationships.")]
		static LightUps() {
			var methodInfo = typeof(TaskCompletionSource<T>).GetMethod(nameof(TaskCompletionSource<int>.TrySetCanceled), new Type[] { typeof(CancellationToken) });
			TrySetCanceled = (Func<TaskCompletionSource<T>, CancellationToken, bool>)Delegate.CreateDelegate(typeof(Func<TaskCompletionSource<T>, CancellationToken, bool>), methodInfo);

			BclAsyncLocalType = LightUps.BclAsyncLocalType?.MakeGenericType(typeof(T));
			if (BclAsyncLocalType != null) {
				BclAsyncLocalCtor = BclAsyncLocalType.GetConstructor(LightUps.EmptyTypeArray);
				BclAsyncLocalValueProperty = BclAsyncLocalType.GetProperty("Value");
				IsAsyncLocalSupported = true;
			}
		}

		/// <summary>
		/// Creates an instance of the BCL AsyncLocal{T} type.
		/// </summary>
		/// <param name="getter">The delegate used to retrieve the Value property.</param>
		/// <param name="setter">The delegate used to set the Value property.</param>
		/// <returns>The constructed instance of AsyncLocal{T}.</returns>
		internal static object CreateAsyncLocal(out Func<T> getter, out Action<T> setter) {
			Verify.Operation(IsAsyncLocalSupported, "AsyncLocal<T> is not supported on this version of the .NET Framework.");
			var instance = BclAsyncLocalCtor.Invoke(null);
			getter = (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), instance, BclAsyncLocalValueProperty.GetMethod);
			setter = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), instance, BclAsyncLocalValueProperty.SetMethod);
			return instance;
		}
	}
}

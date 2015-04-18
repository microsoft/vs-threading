/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Diagnostics.CodeAnalysis;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>
	/// A <see cref="TaskCompletionSource{TResult}"/>-derivative that
	/// does not inline continuations if so configured.
	/// </summary>
	/// <typeparam name="T">The type of the task's resulting value.</typeparam>
	internal class TaskCompletionSourceWithoutInlining<T> : TaskCompletionSource<T> {
		private bool allowInliningContinuations;

		/// <summary>
		/// Initializes a new instance of the <see cref="TaskCompletionSourceWithoutInlining{T}"/> class.
		/// </summary>
		/// <param name="allowInliningContinuations">
		/// <c>true</c> to allow continuations to be inlined; otherwise <c>false</c>.
		/// </param>
		/// <param name="options">
		/// TaskCreationOptions to pass on to the base constructor.
		/// </param>
		internal TaskCompletionSourceWithoutInlining(bool allowInliningContinuations, TaskCreationOptions options = TaskCreationOptions.None)
			: base(AdjustFlags(options, allowInliningContinuations)) {
			this.allowInliningContinuations = allowInliningContinuations;
		}

		/// <summary>
		/// Gets a value indicating whether we can call the completing methods
		/// on the base class on our caller's callstack.
		/// </summary>
		/// <value>
		/// <c>true</c> if our owner allows inlining continuations or .NET 4.6 will ensure they don't inline automatically;
		/// <c>false</c> if our owner does not allow inlining *and* we're on a downlevel version of the .NET Framework.
		/// </value>
		private bool CanCompleteInline {
			get { return this.allowInliningContinuations || TaskCompletionSourceWithoutInlining.IsRunContinuationsAsynchronouslySupported; }
		}

		new internal void SetResult(T value) {
			if (this.CanCompleteInline) {
				base.SetResult(value);
			} else {
				Tuple<TaskCompletionSourceWithoutInlining<T>, T> state = Tuple.Create(this, value);
				ThreadPool.QueueUserWorkItem(
					s => {
						var tuple = (Tuple<TaskCompletionSourceWithoutInlining<T>, T>)s;
						tuple.Item1.SetResult(tuple.Item2);
					},
					state);
			}
		}

		new internal void SetCanceled() {
			if (this.CanCompleteInline) {
				base.SetCanceled();
			} else {
				ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSourceWithoutInlining<T>)state).SetCanceled(), this);
			}
		}

		new internal void SetException(Exception exception) {
			if (this.CanCompleteInline) {
				base.SetException(exception);
			} else {
				ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSourceWithoutInlining<T>)state).SetException(exception), this);
			}
		}

		new internal void TrySetResult(T value) {
			if (this.CanCompleteInline) {
				base.TrySetResult(value);
			} else {
				ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSourceWithoutInlining<T>)state).SetResult(value), this);
			}
		}

		new internal void TrySetCanceled() {
			if (this.CanCompleteInline) {
				base.TrySetCanceled();
			} else {
				ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSourceWithoutInlining<T>)state).SetCanceled(), this);
			}
		}

		new internal void TrySetException(Exception exception) {
			if (this.CanCompleteInline) {
				base.TrySetException(exception);
			} else {
				ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSourceWithoutInlining<T>)state).SetException(exception), this);
			}
		}

		internal void SetResultToDefault() {
			if (this.CanCompleteInline) {
				base.SetResult(default(T));
			} else {
				ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSourceWithoutInlining<T>)state).SetResult(default(T)), this);
			}
		}

		internal void TrySetResultToDefault() {
			if (this.CanCompleteInline) {
				base.TrySetResult(default(T));
			} else {
				ThreadPool.QueueUserWorkItem(state => ((TaskCompletionSourceWithoutInlining<T>)state).SetResult(default(T)), this);
			}
		}

		/// <summary>
		/// Modifies the specified flags to include RunContinuationsAsynchronously
		/// if wanted by the caller and supported by the platform.
		/// </summary>
		/// <param name="options">The base options supplied by the caller.</param>
		/// <param name="allowInliningContinuations"><c>true</c> to allow inlining continuations.</param>
		/// <returns>The possibly modified flags.</returns>
		private static TaskCreationOptions AdjustFlags(TaskCreationOptions options, bool allowInliningContinuations) {
			return (!allowInliningContinuations && TaskCompletionSourceWithoutInlining.IsRunContinuationsAsynchronouslySupported)
				? (options | TaskCompletionSourceWithoutInlining.RunContinuationsAsynchronously)
				: options;
		}
	}

	/// <summary>
	/// Static elements that are not dependant on a generic type parameter.
	/// </summary>
	internal static class TaskCompletionSourceWithoutInlining {
		/// <summary>
		/// A value indicating whether TaskCreationOptions.RunContinuationsAsynchronously
		/// is supported by this version of the .NET Framework.
		/// </summary>
		internal static readonly bool IsRunContinuationsAsynchronouslySupported;

		/// <summary>
		/// The TaskCreationOptions.RunContinuationsAsynchronously flag as found in .NET 4.6
		/// or <see cref="TaskCreationOptions.None"/> if on earlier versions of .NET.
		/// </summary>
		internal static readonly TaskCreationOptions RunContinuationsAsynchronously;

		/// <summary>
		/// Initializes the static members of the <see cref="TaskCompletionSourceWithoutInlining"/> type.
		/// </summary>
		[SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline", Justification = "We have to initialize two fields with a relationship.")]
		static TaskCompletionSourceWithoutInlining() {
			IsRunContinuationsAsynchronouslySupported = Enum.TryParse(
				"RunContinuationsAsynchronously",
				out RunContinuationsAsynchronously);
		}
	}
}

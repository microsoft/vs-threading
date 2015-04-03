namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.CompilerServices;
	using System.Runtime.Remoting.Messaging;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// Stores reference types in the CallContext such that marshaling is safe.
	/// </summary>
	/// <typeparam name="T">The type of value to store.</typeparam>
	public class AsyncLocal<T> where T : class {
		private readonly System.Threading.AsyncLocal<T> asyncLocal = new System.Threading.AsyncLocal<T>();

		/// <summary>
		/// Gets or sets the value to associate with the current CallContext.
		/// </summary>
		public T Value {
			get { return this.asyncLocal.Value; }
			set { this.asyncLocal.Value = value; }
		}
	}
}

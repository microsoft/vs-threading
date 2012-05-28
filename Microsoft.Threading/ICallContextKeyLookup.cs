namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// An interface implemented by values that are to be safely stored via
	/// <see cref="AsyncLocal{T}"/>.
	/// </summary>
	internal interface ICallContextKeyLookup {
		/// <summary>
		/// Gets the value of the readonly field for this object that will be used
		/// exclusively for this instance.
		/// </summary>
		object CallContextValue { get; }
	}
}

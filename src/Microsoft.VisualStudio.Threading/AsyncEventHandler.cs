//-----------------------------------------------------------------------
// <copyright file="AsyncEventHandler.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;

	/// <summary>
	/// An asynchronous event handler.
	/// </summary>
	/// <param name="sender">The sender of the event.</param>
	/// <param name="args">Event arguments.</param>
	/// <returns>A task whose completion signals handling is finished.</returns>
	public delegate Task AsyncEventHandler(object sender, EventArgs args);

	/// <summary>
	/// An asynchronous event handler.
	/// </summary>
	/// <typeparam name="T">The type of <see cref="EventArgs"/></typeparam>
	/// <param name="sender">The sender of the event.</param>
	/// <param name="args">Event arguments.</param>
	/// <returns>A task whose completion signals handling is finished.</returns>
	public delegate Task AsyncEventHandler<T>(object sender, T args) where T : EventArgs;
}

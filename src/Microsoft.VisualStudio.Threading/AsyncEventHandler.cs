/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
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
    public delegate Task AsyncEventHandler(object? sender, EventArgs args);

    /// <summary>
    /// An asynchronous event handler.
    /// </summary>
    /// <typeparam name="TEventArgs">The type of event arguments.</typeparam>
    /// <param name="sender">The sender of the event.</param>
    /// <param name="args">Event arguments.</param>
    /// <returns>A task whose completion signals handling is finished.</returns>
    public delegate Task AsyncEventHandler<TEventArgs>(object? sender, TEventArgs args);
}

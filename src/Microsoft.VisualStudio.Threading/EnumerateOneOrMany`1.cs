// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// Enumerates either a single element or a list of elements.
/// </summary>
/// <typeparam name="T">The type of element to enumerate.</typeparam>
internal struct EnumerateOneOrMany<T> : IEnumerator<T>
{
    /// <summary>
    /// The single element to enumerate, when applicable.
    /// </summary>
    [AllowNull, MaybeNull]
    private T value;

    /// <summary>
    /// The enumerator of the list.
    /// </summary>
    private List<T>.Enumerator enumerator;

    /// <summary>
    /// A value indicating whether a single element or a list of them is being enumerated.
    /// </summary>
    private bool justOne;

    /// <summary>
    /// The position around the lone element being enumerated, when applicable.
    /// </summary>
    private int position;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerateOneOrMany{T}"/> struct.
    /// </summary>
    /// <param name="value">The single value to enumerate.</param>
    internal EnumerateOneOrMany(T value)
    {
        this.value = value;
        this.enumerator = default(List<T>.Enumerator);
        this.justOne = true;
        this.position = -1;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EnumerateOneOrMany{T}"/> struct.
    /// </summary>
    /// <param name="values">The list of values to enumerate.</param>
    internal EnumerateOneOrMany(List<T> values)
    {
        this.value = default(T)!;
        this.enumerator = values.GetEnumerator();
        this.justOne = false;
        this.position = 0; // N/A
    }

    /// <summary>
    /// Gets the current value.
    /// </summary>
    public T Current
    {
        get
        {
            if (this.justOne)
            {
                if (this.position == 0)
                {
                    return this.value!;
                }
                else
                {
                    throw new InvalidOperationException();
                }
            }
            else
            {
                return this.enumerator.Current;
            }
        }
    }

    /// <summary>
    /// Gets the current value.
    /// </summary>
    object? System.Collections.IEnumerator.Current
    {
        get { return this.Current; }
    }

    /// <summary>
    /// Disposes this enumerator.
    /// </summary>
    public void Dispose()
    {
        this.enumerator.Dispose();
    }

    /// <summary>
    /// Advances enumeration to the next element.
    /// </summary>
    public bool MoveNext()
    {
        if (this.justOne)
        {
            if (this.position == -1)
            {
                this.position = 0;
                return true;
            }
            else if (this.position == 0)
            {
                this.position++;
                return false;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return this.enumerator.MoveNext();
        }
    }

    /// <summary>
    /// Resets this enumerator.
    /// </summary>
    void System.Collections.IEnumerator.Reset()
    {
        if (this.justOne)
        {
            this.position = -1;
        }
        else
        {
            ((System.Collections.IEnumerator)this.enumerator).Reset();
        }
    }
}

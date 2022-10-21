// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using global::Windows.Win32.System.Registry;

namespace Microsoft.VisualStudio.Threading;

/// <summary>
/// The various types of data within a registry key that generate notifications
/// when changed.
/// </summary>
/// <remarks>
/// This enum matches the Win32 REG_NOTIFY_CHANGE_* constants.
/// </remarks>
[Flags]
public enum RegistryChangeNotificationFilters
{
    /// <summary>
    /// Notify the caller if a subkey is added or deleted.
    /// </summary>
    Subkey = (int)REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_NAME,

    /// <summary>
    /// Notify the caller of changes to the attributes of the key,
    /// such as the security descriptor information.
    /// </summary>
    Attributes = (int)REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_ATTRIBUTES,

    /// <summary>
    /// Notify the caller of changes to a value of the key. This can
    /// include adding or deleting a value, or changing an existing value.
    /// </summary>
    Value = (int)REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_LAST_SET,

    /// <summary>
    /// Notify the caller of changes to the security descriptor of the key.
    /// </summary>
    Security = (int)REG_NOTIFY_FILTER.REG_NOTIFY_CHANGE_SECURITY,
}

/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

#if DESKTOP

namespace Microsoft.VisualStudio.Threading
{
    using System;

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
        /// Corresponds to Win32 value REG_NOTIFY_CHANGE_NAME.
        /// </summary>
        Subkey = 0x1,

        /// <summary>
        /// Notify the caller of changes to the attributes of the key,
        /// such as the security descriptor information.
        /// Corresponds to Win32 value REG_NOTIFY_CHANGE_ATTRIBUTES.
        /// </summary>
        Attributes = 0x2,

        /// <summary>
        /// Notify the caller of changes to a value of the key. This can
        /// include adding or deleting a value, or changing an existing value.
        /// Corresponds to Win32 value REG_NOTIFY_CHANGE_LAST_SET.
        /// </summary>
        Value = 0x4,

        /// <summary>
        /// Notify the caller of changes to the security descriptor of the key.
        /// Corresponds to Win32 value REG_NOTIFY_CHANGE_SECURITY.
        /// </summary>
        Security = 0x8,
    }
}

#endif

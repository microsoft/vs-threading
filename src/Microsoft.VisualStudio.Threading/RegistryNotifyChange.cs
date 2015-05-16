namespace Microsoft.VisualStudio.Threading
{
    using System;

    /// <summary>
    /// Specifies the kinds of registry key changes to be notified of.
    /// </summary>
    /// <remarks>
    /// This enum matches the Win32 REG_NOTIFY_CHANGE_* constants.
    /// </remarks>
    [Flags]
    public enum RegistryNotifyChange
    {
        /// <summary>
        /// Notify the caller if a subkey is added or deleted.
        /// </summary>
        Name = 0x1,

        /// <summary>
        /// Notify the caller of changes to the attributes of the key,
        /// such as the security descriptor information.
        /// </summary>
        Attributes = 0x2,

        /// <summary>
        /// Notify the caller of changes to a value of the key. This can
        /// include adding or deleting a value, or changing an existing value.
        /// </summary>
        LastSet = 0x4,

        /// <summary>
        /// Notify the caller of changes to the security descriptor of the key.
        /// </summary>
        Security = 0x8
    }
}

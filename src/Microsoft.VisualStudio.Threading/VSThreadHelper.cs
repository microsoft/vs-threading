namespace Microsoft.VisualStudio.Threading
{
#pragma warning disable RS0016 //Add public types and members to the declared API
    /// <summary>
    /// A helper class for integration with Visual Studio.
    /// APIs in this file are intended for Microsoft internal use only.
    /// </summary>
    public static class VSThreadHelper
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public static bool IsMainThreadBlockedByAnyJoinableTask(JoinableTaskContext joinableTaskContext)
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
        {
            return joinableTaskContext?.IsMainThreadBlockedByAnyJoinableTask == true;
        }
    }
#pragma warning restore RS0016 // Add public types and members to the declared API
}

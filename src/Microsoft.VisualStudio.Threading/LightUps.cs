/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading.Tasks;

    /// <summary>
    /// A non-generic class used to store statics that do not vary by generic type argument.
    /// </summary>
    internal static class LightUps
    {
#if DESKTOP
        /// <summary>
        /// The <see cref="OperatingSystem.Version"/> for Windows 8.
        /// </summary>
        private static readonly Version Windows8Version = new Version(6, 2, 9200);
#endif

        /// <summary>
        /// Gets a value indicating whether we execute .NET 4.5 code even on later versions of the Framework.
        /// </summary>
#if DESKTOP
        internal static readonly bool ForceNet45Mode = System.Configuration.ConfigurationManager.AppSettings["Microsoft.VisualStudio.Threading.NET45Mode"] == "true";
#else
        internal const bool ForceNet45Mode = false;
#endif

#if DESKTOP
        /// <summary>
        /// Gets a value indicating whether we execute Windows 7 code even on later versions of Windows.
        /// </summary>
        internal static readonly bool ForceWindows7Mode = System.Configuration.ConfigurationManager.AppSettings["Microsoft.VisualStudio.Threading.Windows7Mode"] == "true";
#endif

#if !ASYNCLOCAL
        /// <summary>
        /// The System.Threading.AsyncLocal open generic type, if present.
        /// </summary>
        /// <remarks>
        /// When running on .NET 4.6, it will be present.
        /// This field will be <c>null</c> on earlier versions of .NET.
        /// </remarks>
        internal static readonly Type BclAsyncLocalType;
#endif

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
        /// Initializes static members of the <see cref="LightUps"/> class.
        /// </summary>
        [SuppressMessage("Microsoft.Performance", "CA1810:InitializeReferenceTypeStaticFieldsInline", Justification = "We have to initialize two fields with a relationship.")]
        static LightUps()
        {
            if (!ForceNet45Mode)
            {
#if TRYSETCANCELEDCT
                IsRunContinuationsAsynchronouslySupported = true;
                RunContinuationsAsynchronously = TaskCreationOptions.RunContinuationsAsynchronously;
#else
                IsRunContinuationsAsynchronouslySupported = Enum.TryParse(
                    "RunContinuationsAsynchronously",
                    out RunContinuationsAsynchronously);
#endif
#if !ASYNCLOCAL
                BclAsyncLocalType = Type.GetType("System.Threading.AsyncLocal`1");
#endif
            }
        }

#if DESKTOP
        /// <summary>
        /// Gets a value indicating whether the current operating system is Windows 8 or later.
        /// </summary>
        internal static bool IsWindows8OrLater
        {
            get
            {
                return !ForceWindows7Mode
                    && Environment.OSVersion.Platform == PlatformID.Win32NT
                    && Environment.OSVersion.Version >= Windows8Version;
            }
        }
#endif
    }
}

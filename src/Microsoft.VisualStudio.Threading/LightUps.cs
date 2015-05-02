/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Configuration;
    using System.Diagnostics.CodeAnalysis;
    using System.Threading.Tasks;

    /// <summary>
    /// A non-generic class used to store statics that do not vary by generic type argument.
    /// </summary>
    internal static class LightUps
    {
        /// <summary>
        /// Gets a value indicating whether we execute .NET 4.5 code even on later versions of the Framework.
        /// </summary>
        internal static readonly bool ForceNet45Mode = ConfigurationManager.AppSettings["Microsoft.VisualStudio.Threading.NET45Mode"] == "true";

        /// <summary>
        /// The System.Threading.AsyncLocal open generic type, if present.
        /// </summary>
        /// <remarks>
        /// When running on .NET 4.6, it will be present. 
        /// This field will be <c>null</c> on earlier versions of .NET.
        /// </remarks>
        internal static readonly Type BclAsyncLocalType;

        /// <summary>
        /// A shareable empty array of Type.
        /// </summary>
        internal static readonly Type[] EmptyTypeArray = new Type[0];

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
                IsRunContinuationsAsynchronouslySupported = Enum.TryParse(
                    "RunContinuationsAsynchronously",
                    out RunContinuationsAsynchronously);
                BclAsyncLocalType = Type.GetType("System.Threading.AsyncLocal`1");
            }
        }
    }
}

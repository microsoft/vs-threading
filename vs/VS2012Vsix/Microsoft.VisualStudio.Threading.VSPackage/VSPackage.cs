namespace Microsoft.VisualStudio.Threading.VSPackage
{
    using System;
    using System.ComponentModel.Design;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Runtime.InteropServices;
    using Microsoft.VisualStudio;
    using Microsoft.VisualStudio.OLE.Interop;
    using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
    using Microsoft.VisualStudio.Shell;
    using Microsoft.VisualStudio.Shell.Interop;
    using Microsoft.Win32;
    using Microsoft;
    using Microsoft.VisualStudio.Threading;

    /// <summary>
    /// The package that owns and exposes the singleton <see cref="JoinableTaskContext"/>
    /// </summary>
    [Guid(VSPackage.PackageGuidString)]
    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1650:ElementDocumentationMustBeSpelledCorrectly", Justification = "pkgdef, VS and vsixmanifest are valid VS terms")]
    public sealed class VSPackage : Package
    {
        /// <summary>
        /// The singleton <see cref="JoinableTaskContext"/>.
        /// </summary>
        private JoinableTaskContext joinableTaskContext;

        /// <summary>
        /// VSPackage GUID string.
        /// </summary>
        public const string PackageGuidString = "144d199a-7913-4117-87bd-714c402dbd1d";

        /// <summary>
        /// Initializes a new instance of the <see cref="VSPackage"/> class.
        /// </summary>
        public VSPackage()
        {
        }

        /// <summary>
        /// Initialization of the package; this method is called right after the package is sited, so this is the place
        /// where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            base.Initialize();

            this.joinableTaskContext = new JoinableTaskContext();

            IServiceContainer container = this;
            container.AddService(typeof(SVsJoinableTaskContext), this.joinableTaskContext, true);
        }
    }
}

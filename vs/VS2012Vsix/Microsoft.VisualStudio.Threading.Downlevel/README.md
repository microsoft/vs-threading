Microsoft Visual Studio Threading Library (downlevel)
======================================================

## Visual Studio versions supported by this VSIX

This VSIX delivers the Microsoft.VisualStudio.Threading.dll assembly to versions of
Visual Studio that did not include it in the base installation. In particular:

* Visual Studio 2012
* Visual Studio 2010 (.NET 4.5 must be installed on the machine)

In addition to delivering the threading library, this VSIX delivers a VS package that provides
the singleton instance of [`JoinableTaskContext`][2] that all VS extensions should share.

## High level steps to redistribute and consume this library and VSIX

To use the Microsoft.VisualStudio.Threading.dll assembly from a VS extension that targets older
versions of VS, a VS extension author should:

1. Install the [`Microsoft.VisualStudio.Threading`][3] NuGet package to their project in order
   to reference the appropriate version of the assembly.
2. When using the JoinableTask-related classes, do *not* create an instance of
   [JoinableTaskContext][2] but rather acquire an instance from the VS Package delivered
   by the VSIX as described in the section below.   
3. Should author an MSI that will chain install this VSIX and does *not* otherwise install
   another copy of Microsoft.VisualStudio.Threading.dll.  
   
## Acquiring an instance of `JoinableTaskContext` 

Acquire the `JoinableTaskContext` singleton using code such as this:

    using Microsoft;
    using Microsoft.VisualStudio.Threading;

    internal static class JoinableTaskContextAcquirer
    {
        /// <summary>
        /// The type serving as the service guid for acquiring an instance of JoinableTaskContext.
        /// </summary>
        [Guid("8767A7D4-ECFC-4627-8FC0-A1685E3B0493")]
        private interface SVsJoinableTaskContext
        {
        }
    
        /// <summary>Acquires the JoinableTaskContext singleton.</summary>
        /// <param name="serviceProvider">The VS Package your extension runs from or some other service provider.</param>
        /// <returns>The singleton JoinableTaskContext; or <c>null</c> if this installation does not support it.</returns>
        internal static JoinableTaskContext Get(IServiceProvider serviceProvider)
        {
            Requires.NotNull(serviceProvider, nameof(serviceProvider));
            
            var jtc = serviceProvider.GetService(typeof(SVsJoinableTaskContext)) as JoinableTaskContext;
            if (jtc == null)
            {
                // VSIX isn't present or some other error occurred.
                // If we're on VS 2013 or later, use the newer approach.
                #if VS2013OrLater
                jtc = GetJoinableTaskContextSingleton2013();
                #endif
            }
                
            return jtc;
        }
        
        #if VS2013OrLater
        private static JoinableTaskContext GetJoinableTaskContextSingleton2013()
        {
            return ThreadHelper.JoinableTaskContext;
        }  
        #endif
    }
    
This instance should be acquired using the above code from your Package.Initialize() method and stored in a field
so it can be queried in a free-threaded way from background threads:

    internal JoinableTaskContext jtc;

    protected override void Initialize() // in your Package-derived class
    {
        base.Initialize();
        this.jtc = JoinableTaskContextAcquirer.Get();
    }
    
Then anywhere else you can use this field to do thread marshaling such as:

    using Microsoft.VisualStudio.Threading;
 
    private async Task SomeLongRunningWorkAsync()
    {
        WorkOnCallersThread();
        await TaskScheduler.Default;
        WorkOnBackgroundThread();
        await this.jtc.Factory.SwitchToMainThreadAsync();
        WorkOnUIThread();
    }

Please review [this MSDN topic][4] and [this blog post][5] for additional guidance when using the JoinableTask-related
functionality of this library.

## Installing this VSIX via your VS extension's MSI installer

This VSIX should be installed by an MSI that links in the .wixlib available from this NuGet package:

    Microsoft.VisualStudio.Threading.DownlevelInstaller

Install this NuGet package into the WiX Toolset project that builds your MSI and follow the steps
in the README file from that NuGet package.

[1]: https://msdn.microsoft.com/en-us/library/microsoft.visualstudio.shell.threadhelper.joinabletaskcontext.aspx
[2]: https://msdn.microsoft.com/en-us/library/microsoft.visualstudio.threading.joinabletaskcontext.aspx
[3]: https://www.nuget.org/packages/Microsoft.VisualStudio.Threading/12.2
[4]: https://msdn.microsoft.com/en-us/library/dn448839.aspx
[5]: https://blogs.msdn.microsoft.com/andrewarnottms/2014/05/07/asynchronous-and-multithreaded-programming-within-vs-using-the-joinabletaskfactory/
[6]: http://wixtoolset.org/documentation/manual/v3/xsd/vs/vsixpackage.html

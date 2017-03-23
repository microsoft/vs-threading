using System;
using System.Resources;
using System.Runtime.InteropServices;

#if NET45
[assembly: NeutralResourcesLanguage("en-US", UltimateResourceFallbackLocation.MainAssembly)]
#else
[assembly: NeutralResourcesLanguage("en-US")]
#endif

[assembly: CLSCompliant(true)]
[assembly: ComVisible(false)]

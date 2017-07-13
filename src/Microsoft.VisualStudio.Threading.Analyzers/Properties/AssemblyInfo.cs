/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

using System;
using System.Resources;
using System.Runtime.InteropServices;

[assembly: CLSCompliant(false)]
[assembly: ComVisible(false)]

#if NET45
[assembly: NeutralResourcesLanguage("en-US", UltimateResourceFallbackLocation.MainAssembly)]
#else
[assembly: NeutralResourcesLanguage("en-US")]
#endif

﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.VisualStudio.Threading.Analyzers;

[DiagnosticAnalyzer(LanguageNames.VisualBasic)]
public sealed class VisualBasicVSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzer : AbstractVSTHRD105AvoidImplicitTaskSchedulerCurrentAnalyzer
{
    protected override LanguageUtils LanguageUtils => VisualBasicUtils.Instance;
}

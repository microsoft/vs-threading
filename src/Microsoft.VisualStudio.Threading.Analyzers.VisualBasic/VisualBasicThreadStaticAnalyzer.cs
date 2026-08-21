// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Microsoft.VisualStudio.Threading.Analyzers;

/// <summary>
/// Verifies that <see cref="System.ThreadStaticAttribute"/> is applied to static fields that are not initialized by the type initializer.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.VisualBasic)]
public sealed class VisualBasicThreadStaticAnalyzer : ThreadStaticAnalyzer
{
}

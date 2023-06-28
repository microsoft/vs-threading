# vs-threading

[![Build Status](https://dev.azure.com/azure-public/vside/_apis/build/status/vs-threading)](https://dev.azure.com/azure-public/vside/_build/latest?definitionId=12)
[![Join the chat at https://gitter.im/vs-threading/Lobby](https://badges.gitter.im/vs-threading/Lobby.svg)](https://gitter.im/vs-threading/Lobby?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

## Microsoft.VisualStudio.Threading

[![NuGet package](https://img.shields.io/nuget/v/Microsoft.VisualStudio.Threading.svg)](https://nuget.org/packages/Microsoft.VisualStudio.Threading)

Async synchronization primitives, async collections, TPL and dataflow extensions. The JoinableTaskFactory allows synchronously blocking the UI thread for async work. This package is applicable to any .NET application (not just Visual Studio).

[Overview documentation](doc/index.md).

[See the full list of features](src/Microsoft.VisualStudio.Threading/README.md).

## Microsoft.VisualStudio.Threading.Analyzers

[![NuGet package](https://img.shields.io/nuget/v/Microsoft.VisualStudio.Threading.Analyzers.svg)](https://nuget.org/packages/Microsoft.VisualStudio.Threading.Analyzers)

Static code analyzer to detect common mistakes or potential issues regarding threading and async coding.

[Diagnostic analyzer rules](doc/analyzers/index.md).

[See the full list of features](src/Microsoft.VisualStudio.Threading.Analyzers.CodeFixes/README.md).

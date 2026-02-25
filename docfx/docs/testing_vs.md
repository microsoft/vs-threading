# Testing Visual Studio extensions and packages

## Considerations when using `JoinableTaskFactory` in code called from unit tests

By default, the [`ThreadHelper.JoinableTaskFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.threadhelper.joinabletaskfactory?view=visualstudiosdk-2019#Microsoft_VisualStudio_Shell_ThreadHelper_JoinableTaskFactory) and [`AsyncPackage.JoinableTaskFactory`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.asyncpackage.joinabletaskfactory?view=visualstudiosdk-2019#Microsoft_VisualStudio_Shell_AsyncPackage_JoinableTaskFactory) properties only works for code running in the VS process.
Unit testing your code that relies on these properties may throw exceptions.

**Important**: Your product code should *never* instantiate its own `JoinableTaskContext`.
Always use the one from `ThreadHelper.JoinableTaskContext`.

To get `ThreadHelper` (and `AsyncPackage`) to work within a unit test process, you may use the [VS SDK Test Framework](https://aka.ms/vssdktestfx).
This framework includes instructions both for MSTest and Xunit to enable your tests to run code that includes usage of `ThreadHelper`, the global `IServiceProvider`, and other APIs that typically only work in the VS process.

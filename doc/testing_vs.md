# Testing Visual Studio extensions and packages

## Avoid calling ThreadHelper.JoinableTaskFactory directly

The ThreadHelper.JoinableTaskFactory property only works for code running in the VS process. If your code is running out of proc (in the vstest.executionengine.exe runner for instance) it won't work. The recommended pattern for testability is that all your code refer to a YourPackage.JoinableTaskFactory property instead of ThreadHelper directly. The YourPackage.Initialize method should initialize this property to ThreadHelper.JoinableTaskFactory. But your unit tests should then overwrite this property with this:

```csharp
var jtc = new JoinableTaskContext();
YourPackage.JoinableTaskFactory = jtc.Factory;
```

That way your product code can run inside or outside VS for testability, and you'll have an instance of JoinableTaskFactory you can use either way.

## Mocking up VS sufficiently so ThreadHelper works outside VS

Note the above technique has the limitation that calling other code that relies on `ThreadHelper.JoinableTaskFactory` (like the `VsTaskLibraryHelper.FileAndForget` extension method) may still fail.

Documentation for how to mock up `ThreadHelper` so that it works within unit tests is coming soon.

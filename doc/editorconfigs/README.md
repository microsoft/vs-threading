# About these .editorconfig files

This folder contains sample .editorconfig files applicable to various project types.

Choose the most applicable .editorconfig file based on your project type and its filename and the introductory comments that may be included as a header in each file.
Append the contents of that file to your own `.editorconfig` file in your repo.

## Use of `warning` severity levels

When the analyzers use the `warning` severity level by default, or when the `.editorconfig` files in this folder set them, it is with the expectation that compilation warnings cause build breaks in PR/CI builds.
Using `warning` allows for a faster inner dev-loop because certain threading violations are permissible while drafting code changes, but _should_ be fixed before code is merged into the main branch.

You can configure your CI/PR build to fail on compilation warnings by setting the `MSBuildTreatWarningsAsErrors` environment or pipeline variable to `true`.

If your repo does _not_ have builds configured to fail on compilation warnings, consider elevating all the warning severites to error severities to ensure these serious issues do not get ignored.

## More about specific project types

While several project types have specific .editorconfig files defined in this folder, some merit some additional explanation and guidance.

### Broadly shared libraries (non-Visual Studio specific)

[SharedLibrary.editorconfig](SharedLibrary.editorconfig)

Libraries that may run in any process, whether they have a main thread or not, should code themselves defensively to avoid any dependency on the main thread so that applications that do not follow `JoinableTaskFactory` rules can avoid deadlocks even when synchronously blocking their main thread using `Task.Wait()` on code running inside your library.
In particular, shared libraries of general interest should _always_ use `.ConfigureAwait(false)` when awaiting on tasks.

[Learn more about authoring libraries following best threading practices](https://microsoft.github.io/vs-threading/docs/library_with_jtf.html).

### Libraries that run inside a JoinableTaskFactory-compliant application

[JTFFocusedLibrary.editorconfig](JTFFocusedLibrary.editorconfig)

These are libraries that always run within a process that follows the JoinableTaskFactory rules, such as the Visual Studio process.
Because these processes _may_ block the main thread using `JoinableTaskFactor.Run` or similar APIs, the most efficient thing for a library to do is _not_ use `.ConfigureAwait(false)` everywhere so that its continuations may resume on the thread that is already blocking on its completion.

### GUI applications and libraries specific to them

[AppWithMainThread.editorconfig](AppWithMainThread.editorconfig)

This essentially captures all projects that are designed specifically to run inside an application that uses a `SynchronizationContext` on its main thread to keep code on the main thread, such as WinForms, WPF, Maui and Avalonia.

These projects are strongly encouraged to include the `Microsoft.VisualStudio.Threading` NuGet package as a dependency and the analyzer modifications below are consistent with that recommendation.

### ASP.NET Core and console applications

[AppWithoutMainThread.editorconfig](AppWithoutMainThread.editorconfig)

### Test projects

[Tests.editorconfig](Tests.editorconfig)

Test projects have a high tendency to define async test methods that are only called by reflection, and the `Async` method name suffix is usually unwelcome there.

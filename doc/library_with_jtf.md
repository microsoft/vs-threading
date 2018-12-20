# Authoring a library with a JoinableTaskFactory dependency

This document describes how to author a library that either itself requires the main thread of its hosting application, or may call out to other code that requires the main thread (e.g. via event handlers). In particular, this document presents ways of obtaining a `JoinableTaskContext` or `JoinableTaskFactory` from library code since only applications (not libraries) should instantiate a `JoinableTaskContext`.

Any instance of `JoinableTaskFactory` is related to a `JoinableTaskContext`. There should only be one instance of `JoinableTaskContext` for a given "main" thread in an application. But since the `Microsoft.VisualStudio.Threading` assembly does not define a static property by which to obtain this singleton, any library that uses `JoinableTaskFactory` must obtain the singleton `JoinableTaskContext` from the application that hosts it. It is the application's responsibility to create this singleton and make it available for the library to consume.

There are a few scenarios a library author may find themself in:

1. [The library targets an app that exposes the `JoinableTaskContext`](#appoffers)
1. [Singleton class with `JoinableTaskContext` property](#singleton)
1. [Accept `JoinableTaskContext` as a constructor argument](#ctor)

If your library is distributed via NuGet, you can help your users follow the threading rules required by `JoinableTaskFactory` by
making sure your users also get the threading analyzers installed into their projects. Do this by modifying your `PackageReference` on the vs-threading library to include `PrivateAssets="none"` so that analyzers are not suppressed:

```xml
<ProjectReference Include="Microsoft.VisualStudio.Threading.Analyzers" Version="[latest-stable-version]" PrivateAssets="none" />
```

It is safe to depend on the latest version of the `Microsoft.VisualStudio.Threading.Analyzers` package.
When referencing the `Microsoft.VisualStudio.Threading` package, the version you select should be no newer than the one used by the hosting application.

## <a name="appoffers"></a>The library targets an app that exposes the `JoinableTaskContext`

### The app uses static properties to expose a `JoinableTaskContext`

When a library targets just one application, that application may expose a `JoinableTaskContext` via a static property on a public class.
For example, Visual Studio exposes the `JoinableTaskContext` from its [`ThreadHelper.JoinableTaskContext`](https://docs.microsoft.com/en-us/dotnet/api/microsoft.visualstudio.shell.threadhelper.joinabletaskcontext?view=visualstudiosdk-2017) property.

Look up the documentation for the application you are extending, or reach out to the application's authors to find out how to obtain the shared instance of `JoinableTaskContext`.

### The app exports a `JoinableTaskContext` via MEF or other IoC container

A library may use MEF or another IoC mechanism to import a `JoinableTaskContext` from its environment. In this way, the library may be rehostable across several applications that export `JoinableTaskContext`.
For example, Visual Studio exports the `JoinableTaskContext` via MEF since 15.3.

### *Some* apps the library targets export `JoinableTaskContext`

When a library runs in multiple apps, only a subset of which actually export a `JoinableTaskContext`, it can be cumbersome to write code to handle its presence and absence all the time. It may be more convenient to instantiate your own instance of `JoinableTaskContext` when in an application that does not export it. *Do this with care*, since there should only be *one* `JoinableTaskContext` for a given main thread in an application. If you create your own because MEF doesn't export it, but the application *does* in fact have a shared instance that is obtainable another way, you could be introducing deadlocks into that application. Be sure to only use this mechanism when you know the app(s) hosting your library either export the `JoinableTaskContext` or have none at all. The following code snippet shows how to conditionally import it, and then ensure you have something you can import everywhere else that is reliable:

```cs
[Export]
internal class ThreadingContext
{
    [ImportingConstructor]
    public ThreadingContext([Import(AllowDefault = true)] JoinableTaskContext joinableTaskContext)
    {
        // If no MEF export is found, we create our own instance.
        // Our private instance will only work if this MEF part is activated on the main thread of the application
        // since creating a JoinableTaskContext captures the thread and SynchronizationContext.
        JoinableTaskContext = joinableTaskContext ?? new JoinableTaskContext();
    }

    /// <summary>
    /// Gets the <see cref="Microsoft.VisualStudio.Threading.JoinableTaskContext" /> associated
    /// with the application if there is one, otherwise a library-local instance.
    /// </summary>
    /// <devremarks>
    /// DO NOT export this property directly, since that will lead to MEF observing TWO exports
    /// in the apps that export this instance already, which will break everyone using this MEF export.
    /// </devremarks>
    public JoinableTaskContext JoinableTaskContext { get; }
}
```

The rest of your library can then import your `ThreadingContext` class:

```cs
internal class SomeUserOfJTF
{
    [Import]
    ThreadingContext ThreadingContext { get; set; }

    public async Task SomeMainThreadMethodAsync()
    {
        await this.ThreadingContext.JoinableTaskContext.SwitchToMainThreadAsync();
        // Do work here.
    }
}
```

## <a name="singleton"></a>Singleton class with `JoinableTaskContext` property

If your library doesn't target any application specifically, the library can indicate in its documentation that to run successfully, a hosting application must set the `JoinableTaskContext` property exposed by the library. This works particularly well if your library has a natural entrypoint class where that `JoinableTaskContext` can be set. This may be a singleton/static class. For example:

```cs
public static class LibrarySettings
{
    private JoinableTaskContext joinableTaskContext;

    /// <summary>
    /// Gets or sets the JoinableTaskContext created on the main thread of the application hosting this library.
    /// </summary>
    public static JoinableTaskContext JoinableTaskContext
    {
        get
        {
            if (this.joinableTaskContext == null)
            {
                // This self-initializer is for when an app does not have a `JoinableTaskContext` to pass to the library.
                // Our private instance will only work if this property getter first runs on the main thread of the application
                // since creating a JoinableTaskContext captures the thread and SynchronizationContext.
                this.joinableTaskContext = new JoinableTaskContext();
            }

            return this.joinableTaskContext;
        }

        set
        {
            Assumes.True(this.joinableTaskContext == null || this.joinableTaskContext == value, "This property has already been set to another value or is set after its value has been retrieved with a self-created value. Set this property once, before it is used elsewhere.");
            this.joinableTaskContext = value;
        }
    }
}
```

This pattern and self-initializer allows all the rest of your library code to assume JTF is always present (so you can use JTF.Run and JTF.RunAsync everywhere w/o feature that JTF will be null), and it mitigates all the deadlocks possible given the host constraints.

Note that when you create your own default instance of JoinableTaskContext (i.e. when the host doesn't), it will consider the thread you're on to be the main thread. If SynchronizationContext.Current != null it will capture it and use it to switch to the main thread when you ask it to (very similar to how VS works today), otherwise any request to SwitchToMainThreadAsync will never switch the thread (since no `SynchronizationContext` was supplied to do so) but otherwise JTF continues to work.

## <a name="ctor"></a>Accept `JoinableTaskContext` as a constructor argument

If your library is app-agnostic (such that it cannot use an app-specific mechanism to obtain an instance of `JoinableTaskContext`) and has no good singleton class on which the app can set the `JoinableTaskContext` instance for the entire library's use, the last option is simply to take a `JoinableTaskContext` instance as a parameter when you need it.

For example, [the `AsyncLazy<T>` constructor accepts a `JoinableTaskFactory` as an optional parameter](https://github.com/Microsoft/vs-threading/blob/027bff027c829cab6be54dbd15551d763199ebf0/src/Microsoft.VisualStudio.Threading/AsyncLazy.cs#L60).
When you make the `JoinableTaskContext`/`JoinableTaskFactory` argument optional, the [VSTHRD012](analyzers/VSTHRD012.md) rule can guide your library's users to specify it if they have it available.

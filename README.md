Microsoft.VisualStudio.Threading
=================================

[![NuGet package](https://img.shields.io/nuget/v/Microsoft.VisualStudio.Threading.svg)](https://nuget.org/packages/Microsoft.VisualStudio.Threading)
[![Build Status](https://andrewarnott.visualstudio.com/OSS/_apis/build/status/vs-threading)](https://andrewarnott.visualstudio.com/OSS/_build/latest?definitionId=16)
[![Join the chat at https://gitter.im/vs-threading/Lobby](https://badges.gitter.im/vs-threading/Lobby.svg)](https://gitter.im/vs-threading/Lobby?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

Analyzers: [![NuGet package](https://img.shields.io/nuget/v/Microsoft.VisualStudio.Threading.Analyzers.svg)](https://nuget.org/packages/Microsoft.VisualStudio.Threading.Analyzers)

## Features

* Async versions of many threading synchronization primitives
  * `AsyncAutoResetEvent`
  * `AsyncBarrier`
  * `AsyncCountdownEvent`
  * `AsyncManualResetEvent`
  * `AsyncReaderWriterLock`
  * `AsyncSemaphore`
  * `ReentrantSemaphore`
* Async versions of very common types
  * `AsyncEventHandler`
  * `AsyncLazy<T>`
  * `AsyncLazyInitializer`
  * `AsyncLocal<T>`
  * `AsyncQueue<T>`
* Await extension methods
  * Await on a `TaskScheduler` to switch to it.
    Switch to a background thread with `await TaskScheduler.Default;`
  * Await on a `Task` with a timeout
  * Await on a `Task` with cancellation
* `JoinableTaskFactory` that allows you to schedule asynchronous or synchronous work
  that does not deadlock with the UI thread even when the UI thread needs to
  synchronously block on the result.

## Documentation

* [Overview documentation](doc/index.md)
* [Diagnostic analyzer rules](doc/analyzers/index.md)

## Supported platforms

* .NET 4.5
* .NET Standard 1.1
* .NET Portable (Profile111)

[1]: https://nuget.org/packages/Microsoft.VisualStudio.Threading "Microsoft.VisualStudio.Threading NuGet package"

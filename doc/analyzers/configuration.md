# Analyzer Configuration

Configuration is provided by way of `AdditionalFiles`, which is an item type
that you may use to add files to your project that provide data for analyzers
such as this one.

The name of the file added to `AdditionalFiles` is significant as it determines
which analyzer(s) will read it. For each use case below, a filename is given that
provides the described data. In each case, a pattern of filenames apply. For example,
if the filename prescribed is `vs-threading.TopicA.txt`, that filename as well
as others that extend that filename are considered. For example, these would all be
read by the analyzer too:

1. vs-threading.TopicA.1.txt
1. vs-threading.TopicA.2.txt
1. vs-threading.TopicA.ABC.txt

These additional files may be provided by the project being built, or other NuGet
packages referenced by the project. A NuGet package that delivers an SDK or library
for example may provide an MSBuild file to be imported by the project that adds these
files.

Files need not have unique filenames. Multiple packages based in different directories may
all use `vs-threading.TopicA.txt` as the filename for their `AdditionalFiles` item.

These files may contain blank lines or comments that start with the `#` character.

## Methods that assert the main thread

Code may assert it is running on the main thread by calling a method that is designed
to throw an exception if called off the main thread. These methods are used by
various analyzers to ensure that single-threaded objects are only invoked on the main thread.
These methods are identified as described below:

**Filename:** `vs-threading.MainThreadAssertingMethods.txt`

**Line format:** `Namespace.TypeName.MethodName`

**Sample:** `Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread`

## Methods that switch to the main thread

Code may switch to the main thread by awaiting a method that is designed
to switch the caller to the main thread if not already there. These methods are used by
various analyzers to ensure that single-threaded objects are only invoked on the main thread.
These methods are identified as described below:

**Filename:** `vs-threading.MainThreadSwitchingMethods.txt`

**Line format:** `Namespace.TypeName.MethodName`

**Sample:** `Microsoft.VisualStudio.Threading.JoinableTaskFactory.SwitchToMainThreadAsync`

## Types that require the main thread

Types that are STA COM objects or otherwise require all invocations to occur on
the main thread of the application can be configured into these analyzers so that
static analysis can help ensure thread safety of the application.
These types are identified as described below:

**Filename:** `vs-threading.TypesRequiringMainThread.txt`

**Line format:** `Namespace.TypeName` or `Namespace.*`

**Sample:** `Microsoft.VisualStudio.Shell.Interop.*`

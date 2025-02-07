# Installing the threading analyzers

The threading analyzers built from this repo and consumable via [the Microsoft.VisualStudio.Threading.Analyzers nuget package][NuGet] are useful for virtually every app library and most libraries.
In particular, projects do not need to be related to Visual Studio in order for these analyzers to apply and improve the quality of your code.

This document outlines how to install the analyzers and offer guidance on how to optimally configure them.

## How to install the threading analyzers

If you are using the `Microsoft.VisualStudio.Threading` NuGet package, you should already have the analyzers installed because they are brought in as a dependency of this package.

Some projects may not want a runtime dependency on `Microsoft.VisualStudio.Threading`, but the analyzers may still apply.
Install the analyzers using any of the methods described on [the package landing page on nuget.org][NuGet].
For example, you might add this tag to your project file:

```xml
<PackageReference Include="Microsoft.VisualStudio.Threading.Analyzers" Version="17.5.22" PrivateAssets="all" />
```

Or even better, add this to some broad `Directory.Build.targets` file so it can apply to all of your projects.

Remember to periodically update the version of the analyzer package you reference.
You should generally use the latest version available, without regard to the version of the application or threading library in use, so you get the best diagnostics.

## Configuring the analyzers

There are [many rules](index.md) in the analyzer package.
The default severity levels for the various rules are not appropriate for every type of project.
To get the best default severity levels for your project type, please review [these editorconfig recommendations](https://github.com/microsoft/vs-threading/blob/main/doc/editorconfigs/README.md) and apply them to your project.

Some analyzers allow for [specialized configuration](configuration.md) that allows you to tailor them to your specific application or library to provide even more value to your team.

## Dealing with suppressions

In some projects we find that some of the threading analyzers have been disabled because they were producing warnings that the project owner did not want to fix at the time.
We generally discourage disabling rules that apply to a project because of the deadlocks that may already exist or that can creep into a codebase over time.
If installing analyzers produces blocking errors or warnings and you cannot fix them all at once, suppress the warnings and schedule time to go back to review them soon.
The recommended way to suppress your "baseline" of warnings is with in-situ `#pragma` suppressions such as:

```css
#pragma warning disable VSTHRD010 // Suppress warning in baseline when installing analyzers -- should review soon
bad.Code();
#pragma warning restore VSTHRD010
```

Suppressing specific occurrences in this way can be done in bulk using an automated C# code fix within Visual Studio.
Doing it for each individual occurrence is better than suppressing the entire rule ID because the analyzers will be allowed to flag newly introduced code while you have not yet reviewed your old code and re-enabled the rule.

If you suspect your project may have suppressed an analyzer rule project-wide, please look in the following common places for broad suppressions and remove them so that your project gets the analyzers run on them:

1. MSBuild project file `<NoWarn>` properties that contain `VSTHRD*` warning IDs.
1. MSBuild project files with `<PackageReference>` to the analyzer package but set `ExcludeAssets="analyzers"`, `ExcludeAssets="all"` or `IncludeAssets="none"` to explicitly turn them off.
1. .ruleset files that decrease severity or turn off `VSTHRD*` rule IDs.
1. .editorconfig files that decrease severity or turn off `VSTHRD*` rule IDs.

## Visual Studio specific analyzers

When a project targets Visual Studio specifically, it should also reference the [Microsoft.VisualStudio.Sdk.Analyzers][SdkAnalyzers] NuGet package.
This package delivers additional analyzers and configures _these_ threading analyzers to be more aware of VS-specific APIs so that better diagnostics can be produced.

```xml
<PackageReference Include="Microsoft.VisualStudio.Sdk.Analyzers" Version="16.10.10" PrivateAssets="all" />
```

Note that although the SDK analyzers automatically brings in the threading analyzers due to a package dependency, you should reference the threading analyzers directly as well to bring in the latest version, since the SDK analyzers ships infrequently and thus brings in older versions of the threading analyzers by default.

[NuGet]: https://www.nuget.org/packages/microsoft.visualstudio.threading.analyzers
[SdkAnalyzers]: https://www.nuget.org/packages/microsoft.visualstudio.sdk.analyzers

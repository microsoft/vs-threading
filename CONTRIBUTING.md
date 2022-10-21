# Contributing

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com)
with any additional questions or comments.

We welcome 3rd party pull requests.
For significant changes we strongly recommend opening an issue to start a design discussion first.

## Best practices

* Use Windows PowerShell or [PowerShell Core][pwsh] (including on Linux/OSX) to run .ps1 scripts.
  Some scripts set environment variables to help you, but they are only retained if you use PowerShell as your shell.

### Prerequisites

* [.NET Core SDK](https://dotnet.microsoft.com/download/dotnet-core/2.2) with the version matching our [global.json](global.json) file. The version you install must be at least the version specified in the global.json file, and must be within the same hundreds version for the 3rd integer: x.y.Czz (x.y.C must match, and zz must be at least as high).
  The easiest way to get this is to run the `init` script at the root of the repo. Use the `-InstallLocality Machine` and approve admin elevation if you wish so the SDK is always discoverable from VS. See the `init` script usage doc for more details.
* Optional: [Visual Studio 2019](https://www.visualstudio.com/)

The only prerequisite for building, testing, and deploying from this repository
is the [.NET SDK](https://get.dot.net/).
You should install the version specified in `global.json` or a later version within
the same major.minor.Bxx "hundreds" band.
For example if 2.2.300 is specified, you may install 2.2.300, 2.2.301, or 2.2.310
while the 2.2.400 version would not be considered compatible by .NET SDK.
See [.NET Core Versioning](https://docs.microsoft.com/dotnet/core/versions/) for more information.

## Package restore

The easiest way to restore packages may be to run `init.ps1` which automatically authenticates
to the feeds that packages for this repo come from, if any.
`dotnet restore` or `nuget restore` also work but may require extra steps to authenticate to any applicable feeds.

## Building

This project can be built with the follow commands from a Visual Studio Developer Command Prompt,
assuming the working directory is the root of this repository:

```ps1
msbuild src
```

[pwsh]: https://docs.microsoft.com/powershell/scripting/install/installing-powershell?view=powershell-6

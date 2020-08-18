# Contributing

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com)
with any additional questions or comments.

We welcome 3rd party pull requests.
For significant changes we strongly recommend opening an issue to start a design discussion first.

## Building

### Prerequisites

* [.NET Core SDK](https://dotnet.microsoft.com/download/dotnet-core/2.2) with the version matching our [global.json](global.json) file. The version you install must be at least the version specified in the global.json file, and must be within the same hundreds version for the 3rd integer: x.y.Czz (x.y.C must match, and zz must be at least as high).
  The easiest way to get this is to run the `init` script at the root of the repo. Use the `-InstallLocality Machine` and approve admin elevation if you wish so the SDK is always discoverable from VS. See the `init` script usage doc for more details.
* Optional: [Visual Studio 2019](https://www.visualstudio.com/)

### Build steps

This project can be built with the follow commands from a Visual Studio Developer Command Prompt,
assuming the working directory is the root of this repository:

```ps1
msbuild src
```

This solution can also be built from within Visual Studio 2019.

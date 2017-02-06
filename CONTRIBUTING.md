Contributing
============

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com)
with any additional questions or comments.

## Building

### Prerequisites required

* [Microsoft Build Tools 2015](https://www.microsoft.com/en-us/download/details.aspx?id=48159) (automatically installed with Visual Studio 2015)

### Better with

* [Visual Studio 2015](https://www.visualstudio.com/en-us)
* [NuProj](http://nuproj.net) extension for Visual Studio

### Important notice when developing with Visual Studio

The NuGet package restore functionality in Visual Studio does not work for this project, which relies
on newer functionality than comes with Visual Studio 2015 Update 3. You should disable automatic
package restore on build in Visual Studio in order to build successfully and have a useful Error List
while developing.

Follow these steps to disable automatic package restore in Visual Studio:

1. Tools -> Options -> NuGet Package Manager -> General
2. *Clear* the checkbox for "Automatically check for missing packages during build in Visual Studio

With this setting, you can still execute a package restore within Visual Studio by right-clicking
on the _solution_ node in Solution Explorer and clicking "Restore NuGet Packages". But do not ever
execute that on this project as that will corrupt the result of `init.ps1`.

## Build steps

This project can be built with the follow commands from a Visual Studio Developer Command Prompt,
assuming the working directory is the root of this repository:

```
.\init
cd src
msbuild
```

After making project or project.json change, or to recover after Visual Studio executes a package restore,
you may need to repeat the init command listed above to re-execute package restore.

This solution can also be built from within Visual Studio 2015, although building the NuGet package
from within Visual Studio requires the NuProj extension as described above.

## Pull requests

We are not yet accepting external pull requests for this repository.

We hope to soon.

This NuGet package targets WiX Toolset projects (.wixproj) that build MSIs.
By installing this NuGet package in such a .wixproj your MSI will install the
Visual Studio 2010 / 2012 downlevel VSIX that provides Microsoft.VisualStudio.Threading.dll
to these older versions of Visual Studio.

This package is not necessary if you target Visual Studio 2013 or later.

At a minimum, your MSI must define these directories to satisfy symbol references in the .wixlib:

    <Fragment>
      <Directory Id="TARGETDIR" Name="SourceDir">
        <Directory Id="ProgramFilesFolder">
          <Directory Id="CommonFilesFolder" />
        </Directory>
      </Directory>
    </Fragment>

You must also reference this component:

    <ComponentRef Id="Microsoft.VisualStudio.Threading.Downlevel" />

You must reference the WixVSExtension from your MSI project.

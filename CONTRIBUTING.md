# Contributing

This project has adopted the [Microsoft Open Source Code of
Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct
FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com)
with any additional questions or comments.

## Best practices

* Use Windows PowerShell or [PowerShell Core][pwsh] (including on Linux/OSX) to run .ps1 scripts.
  Some scripts set environment variables to help you, but they are only retained if you use PowerShell as your shell.

## Prerequisites

All dependencies can be installed by running the `init.ps1` script at the root of the repository
using Windows PowerShell or [PowerShell Core][pwsh] (on any OS).
Some dependencies installed by `init.ps1` may only be discoverable from the same command line environment the init script was run from due to environment variables, so be sure to launch Visual Studio or build the repo from that same environment.
Alternatively, run `init.ps1 -InstallLocality Machine` (which may require elevation) in order to install dependencies at machine-wide locations so Visual Studio and builds work everywhere.

The only prerequisite for building, testing, and deploying from this repository
is the [.NET SDK](https://get.dot.net/).
You should install the version specified in `global.json` or a later version within
the same major.minor.Bxx "hundreds" band.
For example if 2.2.300 is specified, you may install 2.2.300, 2.2.301, or 2.2.310
while the 2.2.400 version would not be considered compatible by .NET SDK.
See [.NET Core Versioning](https://learn.microsoft.com/dotnet/core/versions/) for more information.

## Package restore

The easiest way to restore packages may be to run `init.ps1` which automatically authenticates
to the feeds that packages for this repo come from, if any.
`dotnet restore` or `nuget restore` also work but may require extra steps to authenticate to any applicable feeds.

## Building

This repository can be built on Windows, Linux, and OSX.

Building, testing, and packing this repository can be done by using the standard dotnet CLI commands (e.g. `dotnet build`, `dotnet test`, `dotnet pack`, etc.).

## Testing

You can use `dotnet test` to build and/or test the repo.

There may be tests that are known to be unstable or have special requirements. These can be avoided by running tests using the [dotnet-test-cloud.ps1](tools/dotnet-test-cloud.ps1) script *after* running `dotnet build`.

To build both managed and NativeAOT tests, run `dotnet publish test -c Release`.
Then run `./tools/dotnet-test-cloud.ps1 -Configuration Release -IncludeNativeAOT`.
The traversal project publishes each eligible test framework targeting .NET.
Keep test projects in the solution as well: managed test runs still use the solution, while NativeAOT runs use the traversal's evaluated executable paths.
Test projects can opt out of NativeAOT publishing with `<PublishNativeAOTTests>false</PublishNativeAOTTests>`.
One restore includes all test target frameworks, runtime identifiers, and NativeAOT compiler dependencies.
Managed builds are RID-neutral by default; NativeAOT builds use the SDK's runtime-specific output directories.
For a specified RID, managed execution and native publishing can share the same build outputs.
Test builds keep dynamic code, startup hooks, and event tracing enabled for managed code coverage.
The `ConfigureNativeAOTTestFeatures` target disables those features only in the native compiler's publish-time inputs, without rewriting the managed runtime configuration.
It preserves all other runtime feature options, including invariant globalization, so native compilation and linking use consistent settings.
Test builds use invariant globalization and retain only English satellite resources; RID-specific builds are self-contained.
Shipping libraries targeting .NET opt into NativeAOT compatibility analysis with `IsAotCompatible`.
The `test/AotCompatibilityTest` project complements those analyzers by rooting the shipping assembly and passing it through the NativeAOT compiler during every traversal publish.
Add each shipping assembly that must be validated as a `TrimmerRootAssembly`, and keep this project publishable in `test/dirs.proj`.
Root `Directory.Build.props` supplies project-reference defaults for both traversal and SDK projects that remove the `_IsPublishing` global property for managed dependencies, avoiding duplicate project instances that write to the same outputs during parallel publishing.

## Releases

Use `nbgv tag` to create a tag for a particular commit that you mean to release.
[Learn more about `nbgv` and its `tag` and `prepare-release` commands](https://dotnet.github.io/Nerdbank.GitVersioning/docs/nbgv-cli.html).

Push the tag.

### Azure Pipelines

When your repo builds with Azure Pipelines, use the `azure-pipelines/release.yml` pipeline.
Trigger the pipeline by adding the `auto-release` tag on a run of your main `azure-pipelines.yml` pipeline.

## Tutorial and API documentation

API and hand-written docs are found under the `docfx/` directory and are built by [docfx](https://dotnet.github.io/docfx/).

You can make changes and host the site locally to preview them by switching to that directory and running the `dotnet docfx --serve` command.
After making a change, you can rebuild the docs site while the localhost server is running by running `dotnet docfx` again from a separate terminal.

The `.github/workflows/docs.yml` GitHub Actions workflow publishes the content of these docs to github.io if the workflow itself and [GitHub Pages is enabled for your repository](https://docs.github.com/en/pages/quickstart).

### Documentation validation feed authentication

The `.github/workflows/docs_validate.yml` workflow uses GitHub OIDC to authenticate
as the **azure-public/vside package pull** Entra application before `init.ps1` restores packages.
It requests an Azure DevOps access token and supplies it through
`NuGetPackageSourceCredentials_<source name>` for each Azure Artifacts source in
`nuget.config`, including repositories that use a different name such as
`msft_consumption_public`.
No client secret or PAT is required, and no credentials are written to `nuget.config`.

Run `tools/Configure-GitHubOidc.ps1` from the repository to configure GitHub's immutable
OIDC subject format and create matching Entra federated credentials.
Sign in with `gh auth login` and
`az login --tenant 72f988bf-86f1-41af-91ab-2d7cd011db47 --allow-no-subscriptions` first.
The signed-in identities need repository administration and permission to manage the app's
federated credentials. Use `-WhatIf` to preview without making changes.
The script discovers the calling repository, trusts pull requests plus its default branch,
and can be rerun without duplicating credentials.
Use `-Branches microbuild,release` to add other trusted branches; the default branch
and PR context are always included. The script reports each created or reused credential.
Credential names identify the owner, repository, and PR or branch context, for example
`github-AArnott-Library.Template-pull-request` and
`github-AArnott-Library.Template-branch-main`. Names requiring punctuation replacement
or truncation include a short hash suffix to preserve uniqueness within Entra's name limits.

This changes OIDC subjects for **all workflows** in the repository. Update any other cloud
trust policies (including environment subjects) before running it; existing Entra credentials
are preserved. The script does not configure Azure DevOps feed permissions.

Authentication is enabled only for repositories owned by the `microsoft` organization,
because this Entra tenant requires enterprise-issued GitHub assertions.
Same-repository dependency update PRs, including Renovate and Dependabot, authenticate
using the job's explicit `id-token: write` permission.
Repositories owned by non-microsoft accounts and fork PRs
skip authentication and retain anonymous restore behavior;
new upstream dependencies may still need to be ingested by a trusted run first.
Do not switch this workflow to `pull_request_target` to give untrusted PR code credentials.
Repositories based on this template must configure their own trusted subjects and, if necessary,
update the application and tenant IDs in the workflow.

## Updating dependencies

This repo uses Renovate to keep dependencies current.
Configuration is in the `.github/renovate.json` file.
[Learn more about configuring Renovate](https://docs.renovatebot.com/configuration-options/).

When changing the renovate.json file, follow [these validation steps](https://docs.renovatebot.com/config-validation/).

If Renovate is not creating pull requests when you expect it to, check that the [Renovate GitHub App](https://github.com/apps/renovate) is configured for your account or repo.

## Merging latest from Library.Template

### Maintaining your repo based on this template

The best way to keep your repo in sync with Library.Template's evolving features and best practices is to periodically merge the template into your repo:

```ps1
git fetch
git checkout origin/main
./tools/MergeFrom-Template.ps1
# resolve any conflicts, then commit the merge commit.
git push origin -u HEAD
```

[pwsh]: https://learn.microsoft.com/powershell/scripting/install/installing-powershell

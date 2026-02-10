# Template release notes

This file will describe significant changes in Library.Template as they are introduced, especially if they require special consideration when merging updates into existing repos.
This file is referenced by update-library-template.prompt.md and should remain in place to facilitate future merges, whether done manually or by AI.

## Solution rename

Never leave a Library.slnx file in the repository.
You might even see one there even though this particular merge didn't bring it in.
This can be an artifact of having renamed Library.sln to Library.slnx in the template repo, but ultimately the receiving repo should have only one .sln or .slnx file, with a better name than `Library`.
If you can confidently do so, go ahead and migrate their `.sln` to an `.slnx` with their original name.
If you can't, just delete `Library.slnx` anyway.

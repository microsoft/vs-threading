# This is the default branch in the VS repo that we will
# pull IBC optimization data from when building.

# Simply use the target branch script to get the value.
# In special circumstances it may be useful to split these scripts up,
# or override one but not the other when queuing a build.

& "$PSScriptRoot\InsertTargetBranch.ps1"

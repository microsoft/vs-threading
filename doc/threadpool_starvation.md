# Investigating thread starvation issues

## Summary

Thread starvation are cases where tasks dispatched to managed thread pool doesn't start executing immediately due to other work in the thread pool. In certain cases, the starvation can cause task execution to delay up to a second and if main thread is waiting for the task to complete, this will cause elapsed time regressions as well as responsiveness issues in the product.

A thread pool starvation issue usually manifests itself as a long blocked time on main thread waiting for background work.

## Details about CLR thread pool

In general CLR thread pool is designed short running tasks that don't block the threads and CLR will adjust active worker thread counts to maximize throughput. Because of this, long running tasks that block on network IO, other async tasks can cause problems in how CLR allocates threads. In order to avoid these issues, it is recommended to always use async waits even in thread pool threads to avoid blocking a thread with no actual work being done. There are async APIs available already to do file IO, network IO that should make this easier.

CLR maintains two different statistics for managing thread pool threads. First is the reserved threads which are the actual physical threads created/destroyed in the system. Second is the active thread count which is the subset of the first that are actually used for executing jobs. 

In summary, not all of the thread pools threads visible in traces are actively used by CLR to schedule jobs instead majority of them are kept in reserved state which makes it very hard to analyze starvation from CPU/thread time samples alone without knowing the active thread count available for work.

For the logic of making more threads available for work, CLR will follow a something similar to:
* For anything up to MinThreads, which is equal to number of cores by default, reserved threads will be created on demand and will be made available on demand.
* After that point, if more work is still queued CLR will slowly adjust available thread count, creating reserved threads as needed or promoting existing reserved threads to available state. As this happens CLR will continue to monitor work throughput rate. 
* For cases where available thread count is higher than min count, the CLR algorithm above might also decrease available thread count even if more work is available to see if throughput increases with less parallel work. So the adjustments usually a follow an upward trending zig zag pattern when work is constantly available.
* Once thread pool queue is empty or number of queued items decreases, CLR will quickly retire available threads to reserved status. In traces we see this happening under a second for example if no more work is scheduled. 
* Reserved threads will only be destroyed if they are not used for a while (in order of minutes). This is to avoid constant cost of thread creation/destruction.

On a quad core machine, there will be minimum of 4 active threads at any given time. A starvation may occur anytime these 4 active threads are blockedn a long running operation.

## Investigating the root cause of thread starvation

While CLR has thread pool ETW events to indicate thread starvation, these events may not be included in a trace due to their cost and volume. You can however use Thread Time view to analyze what work was going on in the thread pool during the time main thread was blocked to see if starvation was an issue or not.

1. Open the trace and open Thread Time stacks view filtered to devenv. You need to open the view from PerfView main window instead of scenarios view. The one opened from scenarios view will not show work that was started before the scenario which might be important in this case.
1. In the time view, filter the times to correct time range to the time where main thread was blocked for background task that didn't start executing yet.
1. Make sure clr module symbols are resolved and filter using "IncPaths" to include clr!ThreadpoolMgr::ExecuteWorkRequest frame.
1. This will now show all thread pool threads, some of them will be doing work, some of them will be waiting to be activated by CLR. 
1. In a thread pool exhaustion case, you will have 4 (or a number matching number of CPU cores) threads doing work or blocked on a handle wait as part of some work.
1. Usually after a second another one will start to execute the blocked task or a thread might finish executing with in that second that unblocks the task.
1. Note that per above, CLR keeps a lot of thread pool threads alive but don't actually use them for executing work immediately so just looking at active threads might be misleading.

## RPS specific notes

* RPS machines are quad core machines.
* ETW events that indicate threadpool starvation are not collected on RPS machines due to their cost and volume.

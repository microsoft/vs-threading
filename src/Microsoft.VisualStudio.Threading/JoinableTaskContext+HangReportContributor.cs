/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    partial class JoinableTaskContext : IHangReportContributor
    {
        /// <summary>
        /// Contributes data for a hang report.
        /// </summary>
        /// <returns>The hang report contribution.</returns>
        HangReportContribution IHangReportContributor.GetHangReport()
        {
            return this.GetHangReport();
        }

        /// <summary>
        /// Contributes data for a hang report.
        /// </summary>
        /// <returns>The hang report contribution. Null values should be ignored.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate"), System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate")]
        protected virtual HangReportContribution GetHangReport()
        {
            using (this.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.SyncContextLock)
                {
                    var dgml = CreateTemplateDgml(out XElement nodes, out XElement links);

                    var pendingTasksElements = this.CreateNodesForPendingTasks();
                    var taskLabels = CreateNodeLabels(pendingTasksElements);
                    var pendingTaskCollections = CreateNodesForJoinableTaskCollections(pendingTasksElements.Keys);
                    nodes.Add(pendingTasksElements.Values);
                    nodes.Add(pendingTaskCollections.Values);
                    nodes.Add(taskLabels.Select(t => t.Item1));
                    links.Add(CreatesLinksBetweenNodes(pendingTasksElements));
                    links.Add(CreateCollectionContainingTaskLinks(pendingTasksElements, pendingTaskCollections));
                    links.Add(taskLabels.Select(t => t.Item2));

                    return new HangReportContribution(
                        dgml.ToString(),
                        "application/xml",
                        "JoinableTaskContext.dgml");
                }
            }
        }

        private static XDocument CreateTemplateDgml(out XElement nodes, out XElement links)
        {
            return Dgml.Create(out nodes, out links)
                .WithCategories(
                    Dgml.Category("MainThreadBlocking", "Blocking main thread", background: "#FFF9FF7F", isTag: true),
                    Dgml.Category("NonEmptyQueue", "Non-empty queue", background: "#FFFF0000", isTag: true));
        }

        private static ICollection<XElement> CreatesLinksBetweenNodes(Dictionary<JoinableTask, XElement> pendingTasksElements)
        {
            Requires.NotNull(pendingTasksElements, nameof(pendingTasksElements));

            var links = new List<XElement>();
            foreach (var joinableTaskAndElement in pendingTasksElements)
            {
                foreach (var joinedTask in joinableTaskAndElement.Key.ChildOrJoinedJobs)
                {
                    if (pendingTasksElements.TryGetValue(joinedTask, out XElement joinedTaskElement))
                    {
                        links.Add(Dgml.Link(joinableTaskAndElement.Value, joinedTaskElement));
                    }
                }
            }

            return links;
        }

        private static ICollection<XElement> CreateCollectionContainingTaskLinks(Dictionary<JoinableTask, XElement> tasks, Dictionary<JoinableTaskCollection, XElement> collections)
        {
            Requires.NotNull(tasks, nameof(tasks));
            Requires.NotNull(collections, nameof(collections));

            var result = new List<XElement>();
            foreach (var task in tasks)
            {
                foreach (var collection in task.Key.ContainingCollections)
                {
                    var collectionElement = collections[collection];
                    result.Add(Dgml.Link(collectionElement, task.Value).WithCategories("Contains"));
                }
            }

            return result;
        }

        private static Dictionary<JoinableTaskCollection, XElement> CreateNodesForJoinableTaskCollections(IEnumerable<JoinableTask> tasks)
        {
            Requires.NotNull(tasks, nameof(tasks));

            var collectionsSet = new HashSet<JoinableTaskCollection>(tasks.SelectMany(t => t.ContainingCollections));
            var result = new Dictionary<JoinableTaskCollection, XElement>(collectionsSet.Count);
            int collectionId = 0;
            foreach (var collection in collectionsSet)
            {
                collectionId++;
                var label = string.IsNullOrEmpty(collection.DisplayName) ? "Collection #" + collectionId : collection.DisplayName;
                var element = Dgml.Node("Collection#" + collectionId, label, group: "Expanded")
                    .WithCategories("Collection");
                result.Add(collection, element);
            }

            return result;
        }

        private static List<Tuple<XElement, XElement>> CreateNodeLabels(Dictionary<JoinableTask, XElement> tasksAndElements)
        {
            Requires.NotNull(tasksAndElements, nameof(tasksAndElements));

            var result = new List<Tuple<XElement, XElement>>();
            foreach (var tasksAndElement in tasksAndElements)
            {
                var pendingTask = tasksAndElement.Key;
                var node = tasksAndElement.Value;
                int queueIndex = 0;
                foreach (var pendingTasksElement in pendingTask.MainThreadQueueContents)
                {
                    queueIndex++;
                    var callstackNode = Dgml.Node(node.Attribute("Id").Value + "MTQueue#" + queueIndex, GetAsyncReturnStack(pendingTasksElement));
                    var callstackLink = Dgml.Link(callstackNode, node);
                    result.Add(Tuple.Create(callstackNode, callstackLink));
                }

                foreach (var pendingTasksElement in pendingTask.ThreadPoolQueueContents)
                {
                    queueIndex++;
                    var callstackNode = Dgml.Node(node.Attribute("Id").Value + "TPQueue#" + queueIndex, GetAsyncReturnStack(pendingTasksElement));
                    var callstackLink = Dgml.Link(callstackNode, node);
                    result.Add(Tuple.Create(callstackNode, callstackLink));
                }
            }

            return result;
        }

        private Dictionary<JoinableTask, XElement> CreateNodesForPendingTasks()
        {
            var pendingTasksElements = new Dictionary<JoinableTask, XElement>();
            lock (this.pendingTasks)
            {
                int taskId = 0;
                foreach (var pendingTask in this.pendingTasks)
                {
                    taskId++;

                    string methodName = string.Empty;
                    var entryMethodInfo = pendingTask.EntryMethodInfo;
                    if (entryMethodInfo != null)
                    {
                        methodName = string.Format(
                            CultureInfo.InvariantCulture,
                            " ({0}.{1})",
                            entryMethodInfo.DeclaringType.FullName,
                            entryMethodInfo.Name);
                    }

                    var node = Dgml.Node("Task#" + taskId, "Task #" + taskId + methodName)
                        .WithCategories("Task");
                    if (pendingTask.HasNonEmptyQueue)
                    {
                        node.WithCategories("NonEmptyQueue");
                    }

                    if (pendingTask.State.HasFlag(JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread))
                    {
                        node.WithCategories("MainThreadBlocking");
                    }

                    pendingTasksElements.Add(pendingTask, node);
                }
            }

            return pendingTasksElements;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static string GetAsyncReturnStack(JoinableTaskFactory.SingleExecuteProtector singleExecuteProtector)
        {
            Requires.NotNull(singleExecuteProtector, nameof(singleExecuteProtector));

            var stringBuilder = new StringBuilder();
            try
            {
                foreach (var frame in singleExecuteProtector.WalkAsyncReturnStackFrames())
                {
                    stringBuilder.AppendLine(frame);
                }
            }
            catch (Exception ex)
            {
                // Just eat the exception so we don't crash during a hang report.
                Report.Fail("GetAsyncReturnStackFrames threw exception: ", ex);
            }

            return stringBuilder.ToString().TrimEnd();
        }
    }
}

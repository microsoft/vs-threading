// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Microsoft.VisualStudio.Threading
{
    public partial class JoinableTaskContext : IHangReportContributor
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
        protected virtual HangReportContribution GetHangReport()
        {
            using (this.NoMessagePumpSynchronizationContext.Apply())
            {
                lock (this.SyncContextLock)
                {
                    XDocument? dgml = CreateTemplateDgml(out XElement nodes, out XElement links);

                    Dictionary<JoinableTask, XElement>? pendingTasksElements = this.CreateNodesForPendingTasks();
                    List<Tuple<XElement, XElement>>? taskLabels = CreateNodeLabels(pendingTasksElements);
                    Dictionary<JoinableTaskCollection, XElement>? pendingTaskCollections = CreateNodesForJoinableTaskCollections(pendingTasksElements.Keys);
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
            foreach (KeyValuePair<JoinableTask, XElement> joinableTaskAndElement in pendingTasksElements)
            {
                foreach (JoinableTask? joinedTask in JoinableTaskDependencyGraph.GetAllDirectlyDependentJoinableTasks(joinableTaskAndElement.Key))
                {
                    if (pendingTasksElements.TryGetValue(joinedTask, out XElement? joinedTaskElement))
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
            foreach (KeyValuePair<JoinableTask, XElement> task in tasks)
            {
                foreach (JoinableTaskCollection? collection in task.Key.ContainingCollections)
                {
                    XElement? collectionElement = collections[collection];
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
            foreach (JoinableTaskCollection? collection in collectionsSet)
            {
                collectionId++;
                var label = string.IsNullOrEmpty(collection.DisplayName) ? "Collection #" + collectionId : collection.DisplayName;
                XElement? element = Dgml.Node("Collection#" + collectionId, label, group: "Expanded")
                    .WithCategories("Collection");
                result.Add(collection, element);
            }

            return result;
        }

        private static List<Tuple<XElement, XElement>> CreateNodeLabels(Dictionary<JoinableTask, XElement> tasksAndElements)
        {
            Requires.NotNull(tasksAndElements, nameof(tasksAndElements));

            var result = new List<Tuple<XElement, XElement>>();
            foreach (KeyValuePair<JoinableTask, XElement> tasksAndElement in tasksAndElements)
            {
                JoinableTask? pendingTask = tasksAndElement.Key;
                XElement? node = tasksAndElement.Value;
                int queueIndex = 0;
                foreach (JoinableTaskFactory.SingleExecuteProtector? pendingTasksElement in pendingTask.MainThreadQueueContents)
                {
                    queueIndex++;
                    XElement? callstackNode = Dgml.Node(node.Attribute("Id")!.Value + "MTQueue#" + queueIndex, GetAsyncReturnStack(pendingTasksElement));
                    XElement? callstackLink = Dgml.Link(callstackNode, node);
                    result.Add(Tuple.Create(callstackNode, callstackLink));
                }

                foreach (JoinableTaskFactory.SingleExecuteProtector? pendingTasksElement in pendingTask.ThreadPoolQueueContents)
                {
                    queueIndex++;
                    XElement? callstackNode = Dgml.Node(node.Attribute("Id")!.Value + "TPQueue#" + queueIndex, GetAsyncReturnStack(pendingTasksElement));
                    XElement? callstackLink = Dgml.Link(callstackNode, node);
                    result.Add(Tuple.Create(callstackNode, callstackLink));
                }
            }

            return result;
        }

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

        private Dictionary<JoinableTask, XElement> CreateNodesForPendingTasks()
        {
            var pendingTasksElements = new Dictionary<JoinableTask, XElement>();
            lock (this.pendingTasks)
            {
                int taskId = 0;
                foreach (JoinableTask? pendingTask in this.pendingTasks)
                {
                    taskId++;

                    string methodName = string.Empty;
                    System.Reflection.MethodInfo? entryMethodInfo = pendingTask.EntryMethodInfo;
                    if (entryMethodInfo is object)
                    {
                        methodName = string.Format(
                            CultureInfo.InvariantCulture,
                            " ({0}.{1})",
                            entryMethodInfo.DeclaringType?.FullName,
                            entryMethodInfo.Name);
                    }

                    XElement? node = Dgml.Node("Task#" + taskId, "Task #" + taskId + methodName)
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
    }
}

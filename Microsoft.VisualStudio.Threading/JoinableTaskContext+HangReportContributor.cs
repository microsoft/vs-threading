namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using System.Xml.Linq;

	partial class JoinableTaskContext : IHangReportContributor {
		/// <summary>
		/// Contributes data for a hang report.
		/// </summary>
		/// <returns>The hang report contribution.</returns>
		HangReportContribution IHangReportContributor.GetHangReport() {
			using (NoMessagePumpSyncContext.Default.Apply()) {
				this.SyncContextLock.EnterReadLock();
				try {
					XElement nodes;
					XElement links;
					var dgml = CreateTemplateDgml(out nodes, out links);

					var pendingTasksElements = this.CreateNodesForPendingTasks();
					var taskLabels = this.CreateNodeLabels(pendingTasksElements);
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
				} finally {
					this.SyncContextLock.ExitReadLock();
				}
			}
		}

		private static XDocument CreateTemplateDgml(out XElement nodes, out XElement links) {
			var dgml = Dgml.Create(out nodes, out links);
			var categories = new XElement(
				XName.Get("Categories", Dgml.Namespace),
				new XElement(
					XName.Get("Category", Dgml.Namespace),
					new XAttribute("Id", "MainThreadBlocking"),
					new XAttribute("Label", "Blocking main thread"),
					new XAttribute("Background", "#FFFF7F7F"),
					new XAttribute("IsTag", "True")),
				new XElement(
					XName.Get("Category", Dgml.Namespace),
					new XAttribute("Id", "NonEmptyQueue"),
					new XAttribute("Label", "Non-empty queue"),
					new XAttribute("IsTag", "True")));
			var styles = new XElement(
				XName.Get("Styles", Dgml.Namespace),
				new XElement(
					XName.Get("Style", Dgml.Namespace),
					new XAttribute("TargetType", "Node"),
					new XAttribute("GroupLabel", "NonEmptyQueue"),
					new XElement(
						XName.Get("Condition", Dgml.Namespace),
						new XAttribute("Expression", "HasCategory('NonEmptyQueue')")),
					new XElement(
						XName.Get("Setter", Dgml.Namespace),
						new XAttribute("Property", "Background"),
						new XAttribute("Value", "#FFFF0000"))),
				new XElement(
					XName.Get("Style", Dgml.Namespace),
					new XAttribute("TargetType", "Node"),
					new XAttribute("GroupLabel", "Blocking main thread"),
					new XElement(
						XName.Get("Condition", Dgml.Namespace),
						new XAttribute("Expression", "HasCategory('MainThreadBlocking')")),
					new XElement(
						XName.Get("Setter", Dgml.Namespace),
						new XAttribute("Property", "Background"),
						new XAttribute("Value", "#FFF9FF7F"))));
			dgml.Root.Add(nodes);
			dgml.Root.Add(links);
			dgml.Root.Add(categories);
			dgml.Root.Add(styles);
			return dgml;
		}

		private static ICollection<XElement> CreatesLinksBetweenNodes(Dictionary<JoinableTask, XElement> pendingTasksElements) {
			Requires.NotNull(pendingTasksElements, "pendingTasksElements");

			var links = new List<XElement>();
			foreach (var joinableTaskAndElement in pendingTasksElements) {
				foreach (var joinedTask in joinableTaskAndElement.Key.ChildOrJoinedJobs) {
					XElement joinedTaskElement;
					if (pendingTasksElements.TryGetValue(joinedTask, out joinedTaskElement)) {
						links.Add(
							new XElement(
								XName.Get("Link", Dgml.Namespace),
								new XAttribute("Source", joinableTaskAndElement.Value.Attribute("Id").Value),
								new XAttribute("Target", joinedTaskElement.Attribute("Id").Value)));
					}
				}
			}

			return links;
		}

		private static ICollection<XElement> CreateCollectionContainingTaskLinks(Dictionary<JoinableTask, XElement> tasks, Dictionary<JoinableTaskCollection, XElement> collections) {
			Requires.NotNull(tasks, "tasks");
			Requires.NotNull(collections, "collections");

			var result = new List<XElement>();
			foreach (var task in tasks) {
				foreach (var collection in task.Key.ContainingCollections) {
					var collectionElement = collections[collection];
					var link = new XElement(XName.Get("Link", Dgml.Namespace));
					link.SetAttributeValue("Source", collectionElement.Attribute("Id").Value);
					link.SetAttributeValue("Target", task.Value.Attribute("Id").Value);
					link.WithCategories("Contains");
					result.Add(link);
				}
			}

			return result;
		}

		private static Dictionary<JoinableTaskCollection, XElement> CreateNodesForJoinableTaskCollections(IEnumerable<JoinableTask> tasks) {
			Requires.NotNull(tasks, "tasks");

			var collectionsSet = new HashSet<JoinableTaskCollection>(tasks.SelectMany(t => t.ContainingCollections));
			var result = new Dictionary<JoinableTaskCollection, XElement>(collectionsSet.Count);
			int collectionId = 0;
			foreach (var collection in collectionsSet) {
				collectionId++;
				var element = new XElement(XName.Get("Node", Dgml.Namespace));
				element.SetAttributeValue("Id", "Collection#" + collectionId);
				element.SetAttributeValue("Label", "Collection #" + collectionId);
				element.SetAttributeValue("Group", "Expanded");
				element.WithCategories("Collection");
				result.Add(collection, element);
			}

			return result;
		}

		private Dictionary<JoinableTask, XElement> CreateNodesForPendingTasks() {
			var pendingTasksElements = new Dictionary<JoinableTask, XElement>();
			lock (this.pendingTasks) {
				int taskId = 0;
				foreach (var pendingTask in this.pendingTasks) {
					taskId++;

					string methodName = string.Empty;
					var entryMethodInfo = pendingTask.EntryMethodInfo;
					if (entryMethodInfo != null) {
						methodName = string.Format(
							" ({0}.{1})",
							entryMethodInfo.DeclaringType.FullName,
							entryMethodInfo.Name);
					}

					var node = new XElement(
						XName.Get("Node", Dgml.Namespace),
						new XAttribute("Id", "Task#" + taskId),
						new XAttribute("Label", "Task #" + taskId + methodName));
					node.WithCategories("Task");
					if (pendingTask.HasNonEmptyQueue) {
						node.WithCategories("NonEmptyQueue");
					}

					if (pendingTask.State.HasFlag(JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread)) {
						node.WithCategories("MainThreadBlocking");
					}

					pendingTasksElements.Add(pendingTask, node);
				}
			}

			return pendingTasksElements;
		}

		private List<Tuple<XElement, XElement>> CreateNodeLabels(Dictionary<JoinableTask, XElement> tasksAndElements) {
			Requires.NotNull(tasksAndElements, "tasksAndElements");

			var result = new List<Tuple<XElement, XElement>>();
			foreach (var tasksAndElement in tasksAndElements) {
				var pendingTask = tasksAndElement.Key;
				var node = tasksAndElement.Value;
				int queueIndex = 0;
				foreach (var pendingTasksElement in pendingTask.MainThreadQueueContents) {
					queueIndex++;
					var callstackNode = new XElement(
						XName.Get("Node", Dgml.Namespace),
						new XAttribute("Id", node.Attribute("Id").Value + "MTQueue#" + queueIndex),
						new XAttribute("Label", RepresentCallstack(pendingTasksElement)));
					var callstackLink = new XElement(
						XName.Get("Link", Dgml.Namespace),
						new XAttribute("Source", callstackNode.Attribute("Id").Value),
						new XAttribute("Target", node.Attribute("Id").Value));
					result.Add(Tuple.Create(callstackNode, callstackLink));
				}

				foreach (var pendingTasksElement in pendingTask.ThreadPoolQueueContents) {
					queueIndex++;
					var callstackNode = new XElement(
						XName.Get("Node", Dgml.Namespace),
						new XAttribute("Id", node.Attribute("Id").Value + "TPQueue#" + queueIndex),
						new XAttribute("Label", RepresentCallstack(pendingTasksElement)));
					var callstackLink = new XElement(
						XName.Get("Link", Dgml.Namespace),
						new XAttribute("Source", callstackNode.Attribute("Id").Value),
						new XAttribute("Target", node.Attribute("Id").Value));
					result.Add(Tuple.Create(callstackNode, callstackLink));
				}
			}

			return result;
		}

		private static string RepresentCallstack(JoinableTaskFactory.SingleExecuteProtector singleExecuteProtector) {
			Requires.NotNull(singleExecuteProtector, "singleExecuteProtector");

			var stringBuilder = new StringBuilder();
			var frameIndex = 0;

			try {
				foreach (var frame in singleExecuteProtector.WalkReturnCallstack()) {
					stringBuilder.AppendFormat("{0}. {1}\r\n", frameIndex, frame);
					frameIndex++;
				}
			} catch (Exception e) {
				Report.Fail("RepresentCallstack caught exception: ", e);
			}

			return stringBuilder.ToString().TrimEnd();
		}
	}
}

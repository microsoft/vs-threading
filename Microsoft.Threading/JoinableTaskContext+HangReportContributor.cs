namespace Microsoft.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using System.Xml.Linq;

	partial class JoinableTaskContext : IHangReportContributor {
		/// <summary>
		/// The namespace that all DGML nodes appear in.
		/// </summary>
		private const string DgmlNamespace = "http://schemas.microsoft.com/vs/2009/dgml";

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
			var dgml = new XDocument();
			dgml.Add(
				new XElement(XName.Get("DirectedGraph", DgmlNamespace),
					new XAttribute("Layout", "Sugiyama")));
			nodes = new XElement(XName.Get("Nodes", DgmlNamespace));
			links = new XElement(XName.Get("Links", DgmlNamespace));
			var categories = new XElement(
				XName.Get("Categories", DgmlNamespace),
				new XElement(
					XName.Get("Category", DgmlNamespace),
					new XAttribute("Id", "MainThreadBlocking"),
					new XAttribute("Label", "Blocking main thread"),
					new XAttribute("Background", "#FFFF7F7F"),
					new XAttribute("IsTag", "True")),
				new XElement(
					XName.Get("Category", DgmlNamespace),
					new XAttribute("Id", "NonEmptyQueue"),
					new XAttribute("Label", "Non-empty queue"),
					new XAttribute("IsTag", "True")));
			var styles = new XElement(
				XName.Get("Styles", DgmlNamespace),
				new XElement(
					XName.Get("Style", DgmlNamespace),
					new XAttribute("TargetType", "Node"),
					new XAttribute("GroupLabel", "NonEmptyQueue"),
					new XElement(
						XName.Get("Condition", DgmlNamespace),
						new XAttribute("Expression", "HasCategory('NonEmptyQueue')")),
					new XElement(
						XName.Get("Setter", DgmlNamespace),
						new XAttribute("Property", "Background"),
						new XAttribute("Value", "#FFFF0000"))),
				new XElement(
					XName.Get("Style", DgmlNamespace),
					new XAttribute("TargetType", "Node"),
					new XAttribute("GroupLabel", "Blocking main thread"),
					new XElement(
						XName.Get("Condition", DgmlNamespace),
						new XAttribute("Expression", "HasCategory('MainThreadBlocking')")),
					new XElement(
						XName.Get("Setter", DgmlNamespace),
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
								XName.Get("Link", DgmlNamespace),
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
					var link = new XElement(XName.Get("Link", DgmlNamespace));
					link.SetAttributeValue("Source", collectionElement.Attribute("Id").Value);
					link.SetAttributeValue("Target", task.Value.Attribute("Id").Value);
					AddCategory(link, "Contains");
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
				var element = new XElement(XName.Get("Node", DgmlNamespace));
				element.SetAttributeValue("Id", "Collection#" + collectionId);
				element.SetAttributeValue("Label", "Collection #" + collectionId);
				element.SetAttributeValue("Group", "Expanded");
				AddCategory(element, "Collection");
				result.Add(collection, element);
			}

			return result;
		}

		private static void AddCategory(XElement node, string category) {
			Requires.NotNull(node, "node");
			if (node.Attribute("Category") == null) {
				node.SetAttributeValue("Category", category);
			} else {
				node.Add(new XElement(
					XName.Get("Category", DgmlNamespace),
					new XAttribute("Ref", category)));
			}
		}

		private Dictionary<JoinableTask, XElement> CreateNodesForPendingTasks() {
			var pendingTasksElements = new Dictionary<JoinableTask, XElement>();
			lock (this.pendingTasks) {
				int taskId = 0;
				foreach (var pendingTask in this.pendingTasks) {
					taskId++;

					string methodName = string.Empty;
					if (pendingTask.EntryMethodInfo != null) {
						methodName = string.Format(
							" ({0}.{1})",
							pendingTask.EntryMethodInfo.DeclaringType.FullName,
							pendingTask.EntryMethodInfo.Name);
					}

					var node = new XElement(
						XName.Get("Node", DgmlNamespace),
						new XAttribute("Id", "Task#" + taskId),
						new XAttribute("Label", "Task #" + taskId + methodName));
					AddCategory(node, "Task");
					if (pendingTask.HasNonEmptyQueue) {
						AddCategory(node, "NonEmptyQueue");
					}

					if (pendingTask.State.HasFlag(JoinableTask.JoinableTaskFlags.SynchronouslyBlockingMainThread)) {
						AddCategory(node, "MainThreadBlocking");
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
						XName.Get("Node", DgmlNamespace),
						new XAttribute("Id", node.Attribute("Id").Value + "MTQueue#" + queueIndex),
						new XAttribute("Label", RepresentCallstack(pendingTasksElement)));
					var callstackLink = new XElement(
						XName.Get("Link", DgmlNamespace),
						new XAttribute("Source", callstackNode.Attribute("Id").Value),
						new XAttribute("Target", node.Attribute("Id").Value));
					result.Add(Tuple.Create(callstackNode, callstackLink));
				}

				foreach (var pendingTasksElement in pendingTask.ThreadPoolQueueContents) {
					queueIndex++;
					var callstackNode = new XElement(
						XName.Get("Node", DgmlNamespace),
						new XAttribute("Id", node.Attribute("Id").Value + "TPQueue#" + queueIndex),
						new XAttribute("Label", RepresentCallstack(pendingTasksElement)));
					var callstackLink = new XElement(
						XName.Get("Link", DgmlNamespace),
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

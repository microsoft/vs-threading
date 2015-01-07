namespace Microsoft.VisualStudio.Threading {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading.Tasks;
	using System.Xml.Linq;

	internal static class Dgml {
		/// <summary>
		/// The namespace that all DGML nodes appear in.
		/// </summary>
		internal const string Namespace = "http://schemas.microsoft.com/vs/2009/dgml";

		private static readonly XName NodeName = XName.Get("Node", Namespace);
		private static readonly XName NodesName = XName.Get("Nodes", Namespace);
		private static readonly XName LinkName = XName.Get("Link", Namespace);
		private static readonly XName LinksName = XName.Get("Links", Namespace);
		private static readonly XName StylesName = XName.Get("Styles", Namespace);
		private static readonly XName StyleName = XName.Get("Style", Namespace);

		internal static XDocument Create(out XElement nodes, out XElement links, string layout = "Sugiyama", string direction = null) {
			var dgml = new XDocument();
			dgml.Add(
				new XElement(XName.Get("DirectedGraph", Namespace),
					new XAttribute("Layout", layout)));
			if (direction != null) {
				dgml.Root.Add(new XAttribute("GraphDirection", direction));
			}

			nodes = new XElement(XName.Get("Nodes", Namespace));
			links = new XElement(XName.Get("Links", Namespace));
			dgml.Root.Add(nodes);
			dgml.Root.Add(links);
			dgml.WithCategories(Category("Contains", isContainment: true));
			return dgml;
		}

		private static XElement GetRootElement(this XDocument document, XName name) {
			Requires.NotNull(document, nameof(document));
			Requires.NotNull(name, nameof(name));

			var container = document.Root.Element(name);
			if (container == null) {
				document.Root.Add(container = new XElement(name));
			}

			return container;
		}

		private static XElement GetRootElement(XDocument document, string elementName) {
			Requires.NotNull(document, nameof(document));
			Requires.NotNullOrEmpty(elementName, nameof(elementName));

			return GetRootElement(document, XName.Get(elementName, Namespace));
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
		internal static XDocument WithCategories(this XDocument document, params string[] categories) {
			Requires.NotNull(document, nameof(document));
			Requires.NotNull(categories, nameof(categories));

			GetRootElement(document, "Categories").Add(categories.Select(c => Category(c)));
			return document;
		}

		internal static XDocument WithCategories(this XDocument document, params XElement[] categories) {
			Requires.NotNull(document, nameof(document));
			Requires.NotNull(categories, nameof(categories));

			GetRootElement(document, "Categories").Add(categories);
			return document;
		}

		internal static XElement Node(string id = null, string label = null, string group = null) {
			var element = new XElement(NodeName);

			if (!string.IsNullOrEmpty(id)) {
				element.SetAttributeValue("Id", id);
			}

			if (!string.IsNullOrEmpty(label)) {
				element.SetAttributeValue("Label", label);
			}

			if (!string.IsNullOrEmpty(group)) {
				element.SetAttributeValue("Group", group);
			}

			return element;
		}

		internal static XDocument WithNode(this XDocument document, XElement node) {
			Requires.NotNull(document, nameof(document));
			Requires.NotNull(node, nameof(node));

			var nodes = document.GetRootElement(NodesName);
			nodes.Add(node);
			return document;
		}

		internal static XElement Link(string source, string target) {
			Requires.NotNullOrEmpty(source, nameof(source));
			Requires.NotNullOrEmpty(target, nameof(target));

			return new XElement(
				LinkName,
				new XAttribute("Source", source),
				new XAttribute("Target", target));
		}

		internal static XElement Link(XElement source, XElement target) {
			return Link(source.Attribute("Id").Value, target.Attribute("Id").Value);
		}

		internal static XDocument WithLink(this XDocument document, XElement link) {
			Requires.NotNull(document, nameof(document));
			Requires.NotNull(link, nameof(link));

			var links = document.GetRootElement(LinksName);
			links.Add(link);
			return document;
		}

		internal static XElement Category(string id, string label = null, string background = null, string foreground = null, string icon = null, bool isTag = false, bool isContainment = false) {
			Requires.NotNullOrEmpty(id, nameof(id));

			var category = new XElement(XName.Get("Category", Namespace), new XAttribute("Id", id));
			if (!string.IsNullOrEmpty(label)) {
				category.SetAttributeValue("Label", label);
			}

			if (!string.IsNullOrEmpty(background)) {
				category.SetAttributeValue("Background", background);
			}

			if (!string.IsNullOrEmpty(foreground)) {
				category.SetAttributeValue("Foreground", foreground);
			}

			if (!string.IsNullOrEmpty(icon)) {
				category.SetAttributeValue("Icon", icon);
			}

			if (isTag) {
				category.SetAttributeValue("IsTag", "True");
			}

			if (isContainment) {
				category.SetAttributeValue("IsContainment", "True");
			}

			return category;
		}

		internal static XElement Comment(string label) {
			return Node(label: label).WithCategories("Comment");
		}

		internal static XElement Container(string id, string label = null) {
			return Node(id, label, group: "Expanded");
		}

		internal static XDocument WithContainers(this XDocument document, IEnumerable<XElement> containers) {
			foreach (var container in containers) {
				WithNode(document, container);
			}

			return document;
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
		internal static XElement ContainedBy(this XElement node, XElement container) {
			Requires.NotNull(node, nameof(node));
			Requires.NotNull(container, nameof(container));

			Link(container, node).WithCategories("Contains");
			return node;
		}

		internal static XElement ContainedBy(this XElement node, string containerId, XDocument document) {
			Requires.NotNull(node, nameof(node));
			Requires.NotNullOrEmpty(containerId, nameof(containerId));

			document.WithLink(Link(containerId, node.Attribute("Id").Value).WithCategories("Contains"));
			return node;
		}

		/// <summary>
		/// Adds categories to a DGML node or link.
		/// </summary>
		/// <param name="element">The node or link to add categories to.</param>
		/// <param name="categories">The categories to add.</param>
		/// <returns>The same node that was passed in. To enable "fluent" syntax.</returns>
		internal static XElement WithCategories(this XElement element, params string[] categories) {
			Requires.NotNull(element, nameof(element));

			foreach (var category in categories) {
				if (element.Attribute("Category") == null) {
					element.SetAttributeValue("Category", category);
				} else {
					element.Add(new XElement(
						XName.Get("Category", Namespace),
						new XAttribute("Ref", category)));
				}
			}

			return element;
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
		internal static XDocument WithStyle(this XDocument document, string categoryId, IEnumerable<KeyValuePair<string, string>> properties, string targetType = "Node") {
			Requires.NotNull(document, nameof(document));
			Requires.NotNullOrEmpty(categoryId, nameof(categoryId));
			Requires.NotNull(properties, nameof(properties));
			Requires.NotNullOrEmpty(targetType, nameof(targetType));

			var container = document.Root.Element(StylesName);
			if (container == null) {
				document.Root.Add(container = new XElement(StylesName));
			}

			var style = new XElement(StyleName,
				new XAttribute("TargetType", targetType),
				new XAttribute("GroupLabel", categoryId),
				new XElement(XName.Get("Condition", Namespace),
					new XAttribute("Expression", "HasCategory('" + categoryId + "')")));
			style.Add(properties.Select(p => new XElement(XName.Get("Setter", Namespace), new XAttribute("Property", p.Key), new XAttribute("Value", p.Value))));

			container.Add(style);

			return document;
		}

		[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
		internal static XDocument WithStyle(this XDocument document, string categoryId, string targetType = "Node", string foreground = null, string background = null, string icon = null) {
			var properties = new Dictionary<string, string>();
			if (!string.IsNullOrEmpty(foreground)) {
				properties.Add("Foreground", foreground);
			}

			if (!string.IsNullOrEmpty(background)) {
				properties.Add("Background", background);
			}

			if (!string.IsNullOrEmpty(icon)) {
				properties.Add("Icon", icon);
			}

			return WithStyle(document, categoryId, properties, targetType);
		}
	}
}

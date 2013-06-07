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

		internal static readonly XName NodeName = XName.Get("Node", Namespace);

		internal static readonly XName LinkName = XName.Get("Link", Namespace);

		internal static readonly XName CategoriesName = XName.Get("Categories", Namespace);

		internal static readonly XName StylesName = XName.Get("Styles", Namespace);

		internal static readonly XName StyleName = XName.Get("Style", Namespace);

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
			return dgml;
		}

		internal static XDocument WithCategories(this XDocument document, params string[] categories) {
			Requires.NotNull(document, "document");

			var container = document.Root.Element(CategoriesName);
			if (container == null) {
				document.Root.Add(container = new XElement(CategoriesName));
			}

			container.Add(categories.Select(Category));
			return document;
		}

		internal static XElement Node(string id = null, string label = null) {
			Requires.NotNullOrEmpty(id, "id");
			Requires.NotNullOrEmpty(label, "label");

			var element = new XElement(NodeName);

			if (!string.IsNullOrEmpty(id)) {
				element.SetAttributeValue("Id", id);
			}

			if (!string.IsNullOrEmpty(label)) {
				element.SetAttributeValue("Label", label);
			}

			return element;
		}

		internal static XElement Link(string source, string target) {
			Requires.NotNullOrEmpty(source, "source");
			Requires.NotNullOrEmpty(target, "target");

			return new XElement(
				LinkName,
				new XAttribute("Source", source),
				new XAttribute("Target", target));
		}

		internal static XElement Link(XElement source, XElement target) {
			return Link(source.Attribute("Id").Value, target.Attribute("Id").Value);
		}

		internal static XElement Category(string id) {
			return new XElement(XName.Get("Category", Namespace), new XAttribute("Id", id));
		}

		internal static XElement Comment(string label) {
			return Node(label: label).WithCategories("Comment");
		}

		/// <summary>
		/// Adds categories to a DGML node or link.
		/// </summary>
		/// <param name="element">The node or link to add categories to.</param>
		/// <param name="categories">The categories to add.</param>
		/// <returns>The same node that was passed in. To enable "fluent" syntax.</returns>
		internal static XElement WithCategories(this XElement element, params string[] categories) {
			Requires.NotNull(element, "element");

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

		internal static XDocument WithStyle(this XDocument document, string categoryId, IEnumerable<KeyValuePair<string, string>> properties, string targetType = "Node") {
			Requires.NotNull(document, "document");
			Requires.NotNullOrEmpty(categoryId, "categoryId");
			Requires.NotNull(properties, "properties");
			Requires.NotNullOrEmpty(targetType, "targetType");

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

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Markup;

namespace PhotosWindows.App.Services;

/// <summary>
/// Explains compiled-XAML load failures. WinUI only reports "XAML parsing failed" (E_XAMLPARSEFAILED) for compiled pages;
/// re-parsing the embedded source with <see cref="XamlReader"/> yields the exact element, line and reason.
/// Only used after a failure, for the local log.
/// </summary>
public static partial class XamlDiagnostics
{
    private static readonly string[] EventAttributes =
    [
        "Click", "DragOver", "Drop", "Invoked", "BackRequested", "ItemInvoked", "QuerySubmitted", "SizeChanged", "KeyDown",
        "DoubleTapped", "RightTapped", "ContainerContentChanging", "SelectionChanged", "DragItemsCompleted", "ItemClick",
        "Checked", "Unchecked", "Loaded", "PointerReleased",
    ];

    public static string Explain(string xamlFile)
    {
        try
        {
            return ExplainCore(xamlFile);
        }
        catch (Exception ex)
        {
            // Diagnostics must never take the app down; report what happened instead.
            return $"Diagnosis of {xamlFile} failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string ExplainCore(string xamlFile)
    {
        var assembly = typeof(XamlDiagnostics).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + xamlFile, StringComparison.OrdinalIgnoreCase) || n.EndsWith("/" + xamlFile, StringComparison.OrdinalIgnoreCase));
        if (name is null) return $"No embedded source for {xamlFile}.";
        using var stream = assembly.GetManifestResourceStream(name)!;
        var doc = XDocument.Load(stream, LoadOptions.SetLineInfo);
        var root = doc.Root!;
        var content = root.Name.LocalName == "Window"
            ? root.Elements().First(e => !e.Name.LocalName.StartsWith("Window.", StringComparison.Ordinal))
            : root;
        var clone = new XElement(content);
        foreach (var ns in root.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            if (clone.Attribute(ns.Name) is null) clone.Add(new XAttribute(ns.Name, ns.Value));
        }

        Sanitize(clone);
        var results = new List<string>();
        try
        {
            XamlReader.Load(clone.ToString());
            results.Add($"{xamlFile}: the sanitised source parses correctly (problem is in x:Bind, events or x:Class).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            results.Add($"{xamlFile}: {ex.GetType().Name}: {ex.Message}");
            // Narrow down: try each direct child on its own.
            foreach (var child in AllElements(clone).Take(400))
            {
                var probe = new XElement(child);
                foreach (var ns in clone.Attributes().Where(a => a.IsNamespaceDeclaration)) if (probe.Attribute(ns.Name) is null) probe.Add(new XAttribute(ns.Name, ns.Value));
                if (probe.Name.LocalName.Contains('.', StringComparison.Ordinal)) continue; // property elements cannot be roots
                try
                {
                    XamlReader.Load(probe.ToString());
                }
                catch (Exception inner) when (inner is not OutOfMemoryException)
                {
                    if (!probe.Elements().Any(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal)) || probe.Descendants().Count() < 3)
                    {
                        results.Add($"  failing element <{child.Name.LocalName}> (source line {((System.Xml.IXmlLineInfo)child).LineNumber}): {inner.GetType().Name}: {inner.Message}");
                    }
                }
            }
        }

        return string.Join(Environment.NewLine, results);
    }

    private static IEnumerable<XElement> AllElements(XElement e) => e.Descendants();

    private static void Sanitize(XElement element)
    {
        foreach (var e in element.DescendantsAndSelf().ToList())
        {
            foreach (var a in e.Attributes().ToList())
            {
                var isClass = a.Name.LocalName == "Class" && a.Name.NamespaceName.Contains("xaml", StringComparison.Ordinal);
                var isEvent = EventAttributes.Contains(a.Name.LocalName) && !a.Value.StartsWith('{');
                if (isClass || isEvent || XBindRegex().IsMatch(a.Value)) a.Remove();
            }
        }
    }

    [GeneratedRegex(@"^\{x:Bind")]
    private static partial Regex XBindRegex();
}

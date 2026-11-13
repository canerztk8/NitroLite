using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace NitroLite.Core;

/// <summary>
/// Translates an already-built WPF tree in place. The XAML keeps the Turkish source text, which is also the
/// catalogue key, so no markup extension has to be written into 400 attributes and a text with no translation is
/// left exactly as it is (icons, numbers, "-- °C" and the like are never touched).
/// </summary>
/// <remarks>
/// Call it once after <c>InitializeComponent()</c>. It is idempotent: after the first pass the properties hold the
/// translation, which is not a catalogue key, so a second pass changes nothing.
/// </remarks>
public static class LocXaml
{
    private const int MaxDepth = 64;

    /// <summary>Walks <paramref name="root"/> and replaces every translatable text with its translation.</summary>
    /// <remarks>
    /// A translation is a convenience, never a precondition: if the walk fails the window still opens, in the
    /// source language, and the failure is logged. Losing the window over a missing string would be far worse.
    /// </remarks>
    public static void Apply(DependencyObject? root)
    {
        if (root == null || Loc.Language == Loc.SourceLanguage) return;
        try
        {
            Walk(root, new HashSet<DependencyObject>(), 0);
        }
        catch (Exception ex)
        {
            AppLog.Error("LocXaml", ex.ToString());
        }
    }

    private static void Walk(DependencyObject node, HashSet<DependencyObject> seen, int depth)
    {
        if (depth > MaxDepth || !seen.Add(node)) return;

        // One element that refuses to be translated must not cost the rest of the window its translation.
        try { Translate(node); }
        catch (Exception ex) { AppLog.Error("LocXaml", ex.ToString()); }

        // ItemsControl.Items holds the ComboBoxItems before the drop-down is ever opened, so they are not in the
        // visual tree yet; Content/Header can be a panel that the visual tree does reach, but walking them here
        // keeps the order predictable and the seen set stops the double visit.
        if (node is ItemsControl items)
        {
            foreach (var item in items.Items)
            {
                if (item is DependencyObject d) Walk(d, seen, depth + 1);
            }
        }

        if (node is ContentControl { Content: DependencyObject content }) Walk(content, seen, depth + 1);
        if (node is HeaderedContentControl { Header: DependencyObject header }) Walk(header, seen, depth + 1);
        if (node is HeaderedItemsControl { Header: DependencyObject itemsHeader }) Walk(itemsHeader, seen, depth + 1);

        if (node is not Visual and not Visual3D) return;

        int count;
        try { count = VisualTreeHelper.GetChildrenCount(node); }
        catch { return; }

        for (int i = 0; i < count; i++)
        {
            DependencyObject child;
            try { child = VisualTreeHelper.GetChild(node, i); }
            catch { continue; }
            Walk(child, seen, depth + 1);
        }
    }

    private static void Translate(DependencyObject node)
    {
        switch (node)
        {
            // A TextBlock built from inline content (a Run, a Hyperlink) reports the concatenation through Text, and
            // writing Text would collapse the inlines into one plain run. Those are translated run by run instead.
            case TextBlock textBlock when !IsBound(textBlock, TextBlock.TextProperty):
                if (textBlock.Inlines.Count > 0) TranslateInlines(textBlock.Inlines);
                else Set(textBlock, TextBlock.TextProperty, textBlock.Text);
                break;
            case AccessText accessText when !IsBound(accessText, AccessText.TextProperty):
                Set(accessText, AccessText.TextProperty, accessText.Text);
                break;
        }

        if (node is ContentControl contentControl && contentControl.Content is string content &&
            !IsBound(contentControl, ContentControl.ContentProperty))
        {
            Set(contentControl, ContentControl.ContentProperty, content);
        }

        if (node is HeaderedContentControl headeredContent && headeredContent.Header is string header &&
            !IsBound(headeredContent, HeaderedContentControl.HeaderProperty))
        {
            Set(headeredContent, HeaderedContentControl.HeaderProperty, header);
        }

        if (node is HeaderedItemsControl headeredItems && headeredItems.Header is string itemsHeader &&
            !IsBound(headeredItems, HeaderedItemsControl.HeaderProperty))
        {
            Set(headeredItems, HeaderedItemsControl.HeaderProperty, itemsHeader);
        }

        if (node is Window window && !IsBound(window, Window.TitleProperty))
        {
            Set(window, Window.TitleProperty, window.Title);
        }

        if (node is FrameworkElement element && element.ToolTip is string toolTip &&
            !IsBound(element, FrameworkElement.ToolTipProperty))
        {
            Set(element, FrameworkElement.ToolTipProperty, toolTip);
        }
    }

    /// <summary>A bound property must not be written: SetValue would replace the binding with a fixed text.</summary>
    private static bool IsBound(DependencyObject target, DependencyProperty property) =>
        BindingOperations.GetBindingExpressionBase(target, property) != null;

    /// <summary>
    /// Translates the runs of a TextBlock one by one. <see cref="Span"/> covers Hyperlink and Bold, so a link inside
    /// a sentence keeps its own text and its own styling.
    /// </summary>
    /// <remarks>
    /// The collection is copied first: writing <see cref="Run.Text"/> rebuilds the run's content and bumps the
    /// parent collection's version, which makes an enumerator over it throw on the next step.
    /// </remarks>
    private static void TranslateInlines(InlineCollection inlines)
    {
        var snapshot = new List<Inline>(inlines.Count);
        foreach (var inline in inlines) snapshot.Add(inline);

        foreach (var inline in snapshot)
        {
            if (inline is Run run)
            {
                if (Loc.TryT(run.Text) is { } translated && !string.Equals(translated, run.Text, StringComparison.Ordinal))
                    run.Text = translated;
            }
            else if (inline is Span span)
            {
                TranslateInlines(span.Inlines);
            }
        }
    }

    private static void Set(DependencyObject target, DependencyProperty property, string? current)
    {
        if (string.IsNullOrEmpty(current)) return;
        if (Loc.TryT(current) is { } translated && !string.Equals(translated, current, StringComparison.Ordinal))
        {
            target.SetValue(property, translated);
        }
    }
}

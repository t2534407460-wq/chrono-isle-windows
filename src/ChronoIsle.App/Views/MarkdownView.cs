using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;
using System.IO;
using ChronoIsle.App.Services.Markdown;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Block = System.Windows.Documents.Block;
using Table = System.Windows.Documents.Table;
using List = System.Windows.Documents.List;
using FontFamily = System.Windows.Media.FontFamily;

namespace ChronoIsle.App.Views;

// Markdig owns Markdown parsing; only native WPF elements are created (no HTML/XAML execution).
public sealed class MarkdownView : System.Windows.Controls.RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, e) => ((MarkdownView)d).Render(e.NewValue as string ?? "")));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    Uri? origin;
    public Uri? Origin
    {
        get => origin;
        set { if (origin == value) return; origin = value; if (Markdown.Length > 0) Render(Markdown); }
    }
    public Action<string>? Navigate { get; set; }
    int imageCount;
    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "Brush.TextPrimary");
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PreviewMouseWheel += ForwardWheel;
    }

    void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        if (VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) return;
        for (DependencyObject? p = VisualTreeHelper.GetParent(this); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer scroll) { scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta); e.Handled = true; return; }
    }

    void Render(string value)
    {
        imageCount = 0;
        var document = new FlowDocument { PagePadding = new Thickness(0), ColumnWidth = double.PositiveInfinity,
            FontSize = 14, FontFamily = new FontFamily("Microsoft YaHei UI"), LineHeight = 23 };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Brush.TextPrimary");
        try
        {
            if (value.Length > MarkdownDocuments.MaxBytes) throw new InvalidOperationException("内容过长，请打开源文档查看。");
            var parsed = Markdig.Markdown.Parse(MarkdownDocuments.Prepare(value), MarkdownDocuments.Pipeline);
            AddBlocks(document.Blocks, parsed);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or RegexMatchTimeoutException)
        {
            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new Run("排版暂不可用，以下为原文：\n" + value[..Math.Min(value.Length, MarkdownDocuments.MaxBytes)])));
        }
        Document = document;
    }

    void AddBlocks(BlockCollection target, ContainerBlock blocks)
    {
        foreach (var source in blocks)
        {
            Block? block = source switch
            {
                HeadingBlock h => Paragraph(h.Inline, h.Level),
                ParagraphBlock p => Paragraph(p.Inline),
                CodeBlock c => Code(c),
                ListBlock l => MakeList(l),
                QuoteBlock q => Quote(q),
                Markdig.Extensions.Tables.Table t => MakeTable(t),
                ThematicBreakBlock => new Paragraph(new Run("────────────")),
                _ => null
            };
            if (block is null) continue;
            block.Tag = source.Line + 1;
            target.Add(block);
        }
    }

    Paragraph Paragraph(ContainerInline? inlines, int heading = 0)
    {
        var p = new Paragraph { Margin = new Thickness(0, heading > 0 ? 12 : 0, 0, 10) };
        if (heading > 0) { p.FontSize = heading == 1 ? 23 : heading == 2 ? 19 : 16; p.FontWeight = FontWeights.SemiBold; }
        if (inlines is not null) AddInlines(p.Inlines, inlines);
        return p;
    }

    static Paragraph Code(CodeBlock code)
    {
        var p = new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13, Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 12) };
        p.SetResourceReference(System.Windows.Documents.Paragraph.BackgroundProperty, "Brush.Control");
        return p;
    }

    List MakeList(ListBlock source)
    {
        var list = new List { MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Padding = new Thickness(22, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
        if (source.IsOrdered && int.TryParse(source.OrderedStart, out var start) && start > 0) list.StartIndex = start;
        foreach (var child in source.OfType<ListItemBlock>()) { var item = new ListItem(); AddBlocks(item.Blocks, child); list.ListItems.Add(item); }
        return list;
    }

    Section Quote(QuoteBlock source)
    {
        var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 4, 0, 4), Margin = new Thickness(0, 0, 0, 12) };
        section.SetResourceReference(Section.BorderBrushProperty, "Brush.TextTertiary");
        AddBlocks(section.Blocks, source);
        return section;
    }

    Table MakeTable(Markdig.Extensions.Tables.Table source)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
        var group = new TableRowGroup(); table.RowGroups.Add(group);
        foreach (var row in source.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var result = new System.Windows.Documents.TableRow(); group.Rows.Add(result);
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var target = new System.Windows.Documents.TableCell { Padding = new Thickness(7), BorderThickness = new Thickness(.5), ColumnSpan = Math.Max(1, cell.ColumnSpan) };
                target.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "Brush.Stroke");
                if (row.IsHeader) target.FontWeight = FontWeights.SemiBold;
                AddBlocks(target.Blocks, cell); result.Cells.Add(target);
            }
        }
        return table;
    }

    void AddInlines(InlineCollection target, ContainerInline source)
    {
        for (var node = source.FirstChild; node is not null; node = node.NextSibling)
            switch (node)
            {
                case LiteralInline literal:
                    // Markdig splits Obsidian brackets/pipes into adjacent literal nodes.
                    var text = new System.Text.StringBuilder(literal.Content.ToString());
                    while (node.NextSibling is LiteralInline next) { text.Append(next.Content.ToString()); node = next; }
                    AddLiteral(target, text.ToString()); break;
                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = new FontFamily("Cascadia Mono, Consolas") };
                    run.SetResourceReference(Run.BackgroundProperty, "Brush.Control"); target.Add(run); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterChar == '~' ? new Span { TextDecorations = TextDecorations.Strikethrough } : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis); target.Add(span); break;
                case LinkInline link:
                    if (link.IsImage) { target.Add(ImageInline(link.Url ?? "", link.FirstChild?.ToString() ?? "图片")); break; }
                    var hyperlink = Link(link.Url ?? "");
                    AddInlines(hyperlink.Inlines, link); target.Add(hyperlink); break;
                case AutolinkInline auto: target.Add(Link(auto.Url, auto.Url)); break;
                case TaskList task: target.Add(new Run(task.Checked ? "☑ " : "☐ ")); break;
                case ContainerInline container: AddInlines(target, container); break;
            }
    }

    void AddLiteral(InlineCollection target, string text)
    {
        var offset = 0;
        foreach (Match match in Regex.Matches(text, @"!?\[\[([^\]\r\n]+)\]\]", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            target.Add(new Run(text[offset..match.Index]));
            var parts = match.Groups[1].Value.Split('|', 2);
            var path = parts[0].Split('#', 2);
            var address = path[0];
            if (address.Length > 0 && !System.IO.Path.HasExtension(address)) address += ".md";
            if (path.Length > 1) address += "#" + Uri.EscapeDataString(path[1]);
            target.Add(match.Value.StartsWith('!') && MarkdownImages.IsImagePath(path[0])
                ? ImageInline(address, path[0]) : Link(address, parts.Length > 1 ? parts[1] : parts[0]));
            offset = match.Index + match.Length;
        }
        target.Add(new Run(text[offset..]));
    }

    System.Windows.Documents.Inline ImageInline(string address, string label)
    {
        // Chat messages have no source file; opening their image link remains an explicit action.
        if (Origin is null || imageCount++ >= 32) return Link(address, "图片：" + label + "（点击查看）");
        var origin = Origin;
        var image = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        var caption = new TextBlock { Text = "正在加载图片…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var panel = new StackPanel(); panel.Children.Add(image); panel.Children.Add(caption);
        var button = new Button { Content = panel, Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 10),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left, ToolTip = address, Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(StyleProperty, "Button.Ghost");
        System.Windows.Automation.AutomationProperties.SetAutomationId(button, "MarkdownImage");
        System.Windows.Automation.AutomationProperties.SetName(button, "查看图片：" + label);
        button.Click += (_, _) => MarkdownViewerWindow.OpenLink(Window.GetWindow(this), address, origin);
        CancellationTokenSource? pending = null;
        void Resize()
        {
            button.MaxWidth = Math.Max(40, ActualWidth - 24);
            if (image.Source is not null) image.Width = Math.Min(image.Source.Width, button.MaxWidth);
        }
        SizeChangedEventHandler resize = (_, _) => Resize();
        button.Loaded += async (_, _) =>
        {
            SizeChanged -= resize; SizeChanged += resize; Resize();
            if (image.Source is not null || pending is not null) return;
            using var request = new CancellationTokenSource(); pending = request;
            try
            {
                var bitmap = await MarkdownImages.ReadAsync(MarkdownImages.Resolve(address, origin), true, request.Token);
                if (request.IsCancellationRequested) return;
                image.Source = bitmap; caption.Text = label + " · 点击放大查看"; Resize();
            }
            catch (OperationCanceledException) { if (!request.IsCancellationRequested) caption.Text = "图片读取超时 · 点击重试"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or ArgumentException)
            { if (!request.IsCancellationRequested) caption.Text = "图片无法显示：" + error.Message; }
            finally { if (ReferenceEquals(pending, request)) pending = null; }
        };
        button.Unloaded += (_, _) => { pending?.Cancel(); pending = null; SizeChanged -= resize; };
        return new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center };
    }

    Hyperlink Link(string address, string? label = null)
    {
        var link = new Hyperlink { ToolTip = address };
        link.SetResourceReference(Hyperlink.ForegroundProperty, "Brush.Accent");
        link.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) =>
        {
            // Mouse focus on an already visible link must not scroll the enclosing chat.
            // Its unconstrained RichTextBox otherwise forwards a rectangle at the start of the message.
            if (VerticalScrollBarVisibility == ScrollBarVisibility.Disabled && Mouse.LeftButton == MouseButtonState.Pressed
                && ReferenceEquals(e.TargetObject, link) && e.TargetRect.IsEmpty) e.Handled = true;
        }));
        if (label is not null) link.Inlines.Add(new Run(label));
        link.Click += (_, _) => { if (Navigate is not null) Navigate(address); else MarkdownViewerWindow.OpenLink(Window.GetWindow(this), address, Origin); };
        return link;
    }

    public void JumpTo(string fragment)
    {
        fragment = Uri.UnescapeDataString(fragment.TrimStart('#'));
        if (fragment.Length == 0) { ScrollToHome(); return; }
        var blocks = Document.Blocks.Cast<Block>().ToArray();
        Block? match;
        if (fragment.StartsWith('L') && int.TryParse(fragment.AsSpan(1), out var line))
            match = blocks.LastOrDefault(b => b.Tag is int at && at <= line);
        else match = blocks.FirstOrDefault(b => new TextRange(b.ContentStart, b.ContentEnd).Text.Trim().Equals(fragment, StringComparison.OrdinalIgnoreCase));
        match?.BringIntoView();
    }
}

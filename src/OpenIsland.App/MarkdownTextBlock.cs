using System.Windows;using System.Windows.Controls;using System.Windows.Documents;using System.Windows.Media;
namespace OpenIsland.App;
public sealed class MarkdownTextBlock:TextBlock
{
 public static readonly DependencyProperty MarkdownProperty=DependencyProperty.Register(nameof(Markdown),typeof(string),typeof(MarkdownTextBlock),new PropertyMetadata("",Changed));
 public string Markdown{get=>(string)GetValue(MarkdownProperty);set=>SetValue(MarkdownProperty,value);}
 public MarkdownTextBlock(){TextWrapping=TextWrapping.Wrap;Foreground=new SolidColorBrush(Color.FromRgb(242,242,247));LineHeight=22;}
 static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e)=>((MarkdownTextBlock)d).Render(e.NewValue as string??"");
 void Render(string value){Inlines.Clear();var lines=value.Replace("\r","").Split('\n');foreach(var raw in lines){var line=raw.TrimEnd();if(line=="---"){Inlines.Add(new Run("────────────────"){Foreground=new SolidColorBrush(Color.FromRgb(110,110,115))});Inlines.Add(new LineBreak());continue;}var heading=line.StartsWith("# ")||line.StartsWith("## ")||line.StartsWith("### ");var text=heading?line.TrimStart('#',' '):line; if(line.StartsWith("- ")||line.StartsWith("* ")){Inlines.Add(new Run("• "){Foreground=new SolidColorBrush(Color.FromRgb(160,160,170))});text=line[2..];}AddInline(text,heading);Inlines.Add(new LineBreak());}}
 void AddInline(string text,bool heading){var parts=text.Split("**");for(var i=0;i<parts.Length;i++){var run=new Run(parts[i]);if(heading||i%2==1)run.FontWeight=FontWeights.SemiBold;if(heading)run.FontSize=15;Inlines.Add(run);}}
}
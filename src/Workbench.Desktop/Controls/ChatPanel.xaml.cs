using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace Workbench.Desktop.Controls;

public partial class ChatPanel : UserControl
{
    public ChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is WorkbenchViewModel old) { old.PropertyChanged -= Changed; old.Conversation.CollectionChanged -= ConversationChanged; }
            if (e.NewValue is WorkbenchViewModel next) { next.PropertyChanged += Changed; next.Conversation.CollectionChanged += ConversationChanged; }
        };
    }
    private void ConversationChanged(object? sender, NotifyCollectionChangedEventArgs e) => Follow();
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(WorkbenchViewModel.StreamingReply)) Follow(); }
    private void Follow() { if (Transcript.VerticalOffset >= Transcript.ScrollableHeight - 100) _ = Dispatcher.BeginInvoke(new Action(Transcript.ScrollToEnd)); }
}

using System.Windows;
using System.Windows.Controls;

namespace Workbench.Desktop.Themes;

internal static class DarkChrome
{
    public static void Initialize()
    {
        // WPF's built-in text editor menus use internal subclasses. Implicit styles match exact
        // types, so give those subclasses the same resources as ordinary menus and menu items.
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                var menu = (ContextMenu)sender;
                StyleSubclass(menu, typeof(ContextMenu)); StyleItems(menu);
            }));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler((sender, _) => StyleItems((MenuItem)sender)));
    }
    private static void StyleSubclass(FrameworkElement element, Type baseType)
    {
        if (element.GetType() != baseType) element.SetResourceReference(FrameworkElement.StyleProperty, baseType);
    }
    private static void StyleItems(ItemsControl owner)
    {
        foreach (var item in owner.Items.OfType<MenuItem>())
        {
            StyleSubclass(item, typeof(MenuItem)); StyleItems(item);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.App;

public sealed class SessionPicker : Window
{
    public SessionInfo? Selected { get; private set; }
    public SessionPicker(IReadOnlyList<SessionInfo> sessions)
    {
        Style=(Style)FindResource(typeof(Window));
        Title="Локальные сессии Codex"; Width=1000; Height=620; MinWidth=800; MinHeight=420; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel { Margin=new Thickness(20) };
        var header=new StackPanel(); DockPanel.SetDock(header,Dock.Top);
        header.Children.Add(new TextBlock { Text="Выберите точную сессию", FontSize=22, FontWeight=FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text="Доступны только сессии текущего нативного Windows CLI. WSL и другие CODEX_HOME требуют исходного клиента.",Margin=new Thickness(0,8,0,10) });
        var search=new TextBox { Margin=new Thickness(0,0,0,14),ToolTip="Поиск по названию, ID и папке" }; header.Children.Add(search); root.Children.Add(header);
        var footer=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right }; DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        var grid=new DataGrid { IsReadOnly=true,AutoGenerateColumns=false,SelectionMode=DataGridSelectionMode.Single,ItemsSource=sessions,Background=(System.Windows.Media.Brush)FindResource("CanvasBrush"),Foreground=(System.Windows.Media.Brush)FindResource("TextBrush"),RowBackground=(System.Windows.Media.Brush)FindResource("PanelBrush"),AlternatingRowBackground=(System.Windows.Media.Brush)FindResource("CanvasBrush"),CanUserAddRows=false,CanUserDeleteRows=false,GridLinesVisibility=DataGridGridLinesVisibility.None,RowHeaderWidth=0,BorderThickness=new Thickness(0) };
        var headerStyle=new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(BackgroundProperty,FindResource("BorderBrush"))); headerStyle.Setters.Add(new Setter(ForegroundProperty,FindResource("TextBrush"))); headerStyle.Setters.Add(new Setter(PaddingProperty,new Thickness(8,10,8,10))); grid.ColumnHeaderStyle=headerStyle;
        foreach (var (title,path,width) in new[] { ("Название","Name",250),("ID","Id",260),("Папка проекта","Cwd",240),("Последняя активность","LocalUpdated",140),("Статус","StatusLabel",130),("Источник","Source",85) })
            grid.Columns.Add(new DataGridTextColumn { Header=title,Binding=new Binding(path),Width=width });
        var choose=new Button { Content="Выбрать",IsDefault=true }; choose.Click+=(_,_)=> { if (grid.SelectedItem is SessionInfo t) { Selected=t; DialogResult=true; } }; footer.Children.Add(choose);
        var cancel=new Button { Content="Отмена",IsCancel=true }; footer.Children.Add(cancel);
        grid.MouseDoubleClick+=(_,_)=> { if (grid.SelectedItem is SessionInfo t) { Selected=t; DialogResult=true; } };
        search.TextChanged+=(_,_)=> { var value=search.Text; grid.ItemsSource=sessions.Where(t=>t.Name.Contains(value,StringComparison.OrdinalIgnoreCase) || t.Id.Contains(value,StringComparison.OrdinalIgnoreCase) || t.Cwd.Contains(value,StringComparison.OrdinalIgnoreCase)).ToList(); };
        root.Children.Add(grid); Content=root;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CodexAutoContinue.Core;

namespace CodexAutoContinue.App;

public sealed class ApprovalDialog : Window
{
    public object? Response { get; private set; }
    private readonly StackPanel _body=new();
    private readonly WrapPanel _buttons=new() { Margin=new Thickness(0,18,0,0) };
    public static string RequestTitle(string method) => method switch
    {
        "item/commandExecution/requestApproval"=>"Разрешение на выполнение команды / доступ к сети",
        "item/fileChange/requestApproval"=>"Разрешение на изменение файлов",
        "item/permissions/requestApproval"=>"Запрос дополнительных разрешений",
        "item/tool/requestUserInput"=>"Вопрос Codex", "mcpServer/elicitation/request"=>"Запрос подключённого сервиса", _=>"Запрос CLI"
    };
    public ApprovalDialog(ServerRequest request)
    {
        Style=(Style)FindResource(typeof(Window));
        Title=RequestTitle(request.Method); Width=740; Height=650; MinHeight=380; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var root=new DockPanel { Margin=new Thickness(24) }; DockPanel.SetDock(_buttons,Dock.Bottom); root.Children.Add(_buttons);
        _body.Children.Add(new TextBlock { Text=Title,FontSize=21,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14) });
        _body.Children.Add(new TextBlock { Text="Приложение ждёт вашего решения. Самостоятельное разрешение и повторный запуск не выполняются.",Margin=new Thickness(0,0,0,16) });
        root.Children.Add(new ScrollViewer { Content=_body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto }); Content=root;
        var p=request.Params;
        var reason=p.Str("reason",p.Str("message")); if (reason.Length>0) _body.Children.Add(new TextBlock { Text=Redactor.Clean(reason),Margin=new Thickness(0,0,0,12) });
        switch (request.Method)
        {
            case "item/commandExecution/requestApproval":
                Details(p);
                // Honor a server-provided list if a future/older protocol includes it.
                var available=p.Array("availableDecisions");
                foreach (var (label,decision) in new[] { ("Разрешить один раз","accept"),("Разрешить в этой сессии","acceptForSession"),("Отказать","decline"),("Отменить запуск","cancel") })
                    if (available.Length == 0 || available.Any(x=>x.ValueKind == JsonValueKind.String && x.GetString() == decision)) AddButton(label,()=>new { decision });
                if (p.Obj("proposedExecpolicyAmendment").ValueKind == JsonValueKind.Array)
                {
                    var amendment=p.Obj("proposedExecpolicyAmendment").Clone();
                    AddButton("Разрешить с предложенным правилом",()=>new { decision=new { acceptWithExecpolicyAmendment=new { execpolicy_amendment=amendment } } });
                }
                foreach (var amendment in p.Array("proposedNetworkPolicyAmendments"))
                {
                    var copy=amendment.Clone();
                    AddButton("Применить правило сети: "+Redactor.Clean(copy.GetRawText()),()=>new { decision=new { applyNetworkPolicyAmendment=new { network_policy_amendment=copy } } });
                }
                break;
            case "item/fileChange/requestApproval":
                Details(p);
                foreach (var (label,decision) in new[] { ("Разрешить один раз","accept"),("Разрешить в этой сессии","acceptForSession"),("Отказать","decline"),("Отменить запуск","cancel") }) AddButton(label,()=>new { decision });
                break;
            case "item/permissions/requestApproval":
                Details(p);
                var permissions=p.Obj("permissions");
                var grants=new Dictionary<string,CheckBox>();
                if (permissions.ValueKind == JsonValueKind.Object)
                    foreach (var part in permissions.EnumerateObject().Where(x=>x.Value.ValueKind != JsonValueKind.Null))
                    { var check=new CheckBox { Content=part.Name == "network" ? "Разрешить запрошенный доступ к сети" : "Разрешить запрошенный доступ к файловой системе",IsChecked=false }; grants.Add(part.Name,check); _body.Children.Add(check); }
                var sessionScope=new CheckBox { Content="Сохранить выбранные разрешения до конца сессии",IsChecked=false }; _body.Children.Add(sessionScope);
                AddButton("Разрешить выбранное",()=>new { permissions=grants.Where(x=>x.Value.IsChecked == true).ToDictionary(x=>x.Key,x=>permissions.Obj(x.Key).Clone()),scope=sessionScope.IsChecked == true ? "session" : "turn" });
                AddButton("Отказать",()=>new { permissions=new { },scope="turn" });
                break;
            case "item/tool/requestUserInput":
                Questions(p); break;
            case "mcpServer/elicitation/request":
                Elicitation(p); break;
        }
    }
    private void Details(JsonElement p)
    {
        var entries=new List<string>();
        if (p.OptionalStr("command") is { } cmd) entries.Add("Команда:\n"+cmd);
        if (p.OptionalStr("cwd") is { } cwd) entries.Add("Папка:\n"+cwd);
        if (p.OptionalStr("grantRoot") is { } grant) entries.Add("Разрешение на изменение файлов:\n"+grant);
        foreach (var change in p.Array("_previewChanges")) entries.Add("Изменение файла: "+change.Str("path")+"\n"+change.Str("diff"));
        if (p.Obj("networkApprovalContext").ValueKind == JsonValueKind.Object) entries.Add("Доступ к сети:\n"+Pretty(p.Obj("networkApprovalContext")));
        if (p.Obj("permissions").ValueKind == JsonValueKind.Object) entries.Add("Запрошенные разрешения:\n"+Pretty(p.Obj("permissions")));
        if (p.Obj("proposedExecpolicyAmendment").ValueKind == JsonValueKind.Array) entries.Add("Предложенное правило:\n"+Pretty(p.Obj("proposedExecpolicyAmendment")));
        if (p.Array("proposedNetworkPolicyAmendments").Length>0) entries.Add("Предложенные правила сети:\n"+Pretty(p.Obj("proposedNetworkPolicyAmendments")));
        if (entries.Count == 0) entries.Add("Изменение файлов выбранной задачи. Решение относится к текущему запросу CLI.");
        var text=string.Join("\n\n",entries);
        _body.Children.Add(new TextBox { Text=Redactor.Clean(text),IsReadOnly=true,AcceptsReturn=true,MinHeight=120,MaxHeight=290,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontFamily=new System.Windows.Media.FontFamily("Consolas"),FontSize=12 });
    }
    private static string Pretty(JsonElement value) => JsonSerializer.Serialize(value,new JsonSerializerOptions { WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private void AddButton(string label,Func<object> value)
    {
        var b=new Button { Content=label }; b.Click+=(_,_)=>
        { try { Response=value(); DialogResult=true; } catch (Exception e) { MessageBox.Show(this,e.Message,"Проверьте ответ"); } }; _buttons.Children.Add(b);
    }
    private void Questions(JsonElement p)
    {
        var readers=new Dictionary<string,Func<string>>();
        foreach (var q in p.Array("questions"))
        {
            _body.Children.Add(new TextBlock { Text=Redactor.Clean(q.Str("question")),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,16,0,8) });
            var options=q.Array("options");
            foreach (var opt in options) _body.Children.Add(new TextBlock { Text=opt.Str("label")+": "+opt.Str("description"),Margin=new Thickness(0,0,0,4) });
            if (q.Bool("isSecret") == true)
            { var password=new PasswordBox { Padding=new Thickness(8),Margin=new Thickness(0,8,0,0) }; _body.Children.Add(password); readers[q.Str("id")]=()=>password.Password; }
            else
            {
                var box=new TextBox { Margin=new Thickness(0,8,0,0) }; _body.Children.Add(box);
                if (options.Length>0)
                {
                    var choose=new ComboBox { ItemsSource=options.Select(o=>o.Str("label")).ToList(),Margin=new Thickness(0,8,0,0),IsEditable=false };
                    choose.SelectionChanged+=(_,_)=> { if (choose.SelectedItem is string label) box.Text=label; }; _body.Children.Add(choose);
                }
                readers[q.Str("id")]=()=>box.Text;
            }
        }
        AddButton("Отправить ответ",()=>
        {
            var result=readers.ToDictionary(x=>x.Key,x=>new { answers=new[] { x.Value() } });
            if (result.Values.Any(x=>string.IsNullOrWhiteSpace(x.answers[0]))) throw new InvalidOperationException("Ответьте на каждый вопрос.");
            return new { answers=result };
        });
    }
    private void Elicitation(JsonElement p)
    {
        if (p.Str("mode") == "url")
        {
            var url=p.Str("url"); _body.Children.Add(new TextBlock { Text=url,Margin=new Thickness(0,8,0,8) });
            var open=new Button { Content="Открыть адрес сервиса" }; open.Click+=(_,_)=> { if (Uri.TryCreate(url,UriKind.Absolute,out var uri) && uri.Scheme is "https" or "http") Process.Start(new ProcessStartInfo(url) { UseShellExecute=true }); }; _body.Children.Add(open);
            AddButton("Подтвердить после действия в браузере",()=>new { action="accept", content=(object?)null, _meta=(object?)null });
        }
        else
        {
            var schema=p.Obj("requestedSchema"); var required=schema.Array("required").Where(x=>x.ValueKind == JsonValueKind.String).Select(x=>x.GetString()!).ToHashSet();
            var readers=new Dictionary<string,Func<object?>>(); var props=schema.Obj("properties");
            if (props.ValueKind == JsonValueKind.Object)
                foreach (var prop in props.EnumerateObject())
                {
                    var key=prop.Name; var spec=prop.Value.Clone();
                    _body.Children.Add(new TextBlock { Text=Redactor.Clean(spec.Str("title",key)+(required.Contains(key) ? " *" : "")),Margin=new Thickness(0,12,0,5) });
                    if (spec.Str("type") == "boolean") { var check=new CheckBox { Content=spec.Str("description",key) }; _body.Children.Add(check); readers[key]=()=>check.IsChecked == true; }
                    else if (spec.Array("enum").Length>0)
                    { var combo=new ComboBox { ItemsSource=spec.Array("enum").Select(x=>x.ToString()).ToList() }; _body.Children.Add(combo); readers[key]=()=>combo.SelectedItem; }
                    else
                    {
                        var box=new TextBox(); _body.Children.Add(box);
                        readers[key]=()=>
                        {
                            if (string.IsNullOrWhiteSpace(box.Text)) return null;
                            if (spec.Str("type") == "integer") return long.TryParse(box.Text,out var integer) ? integer : throw new InvalidOperationException(key+": введите целое число.");
                            if (spec.Str("type") == "number") return double.TryParse(box.Text,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var number) ? number : throw new InvalidOperationException(key+": введите число.");
                            if (spec.Str("type") is "object" or "array") return JsonDocument.Parse(box.Text).RootElement.Clone();
                            return box.Text;
                        };
                    }
                }
            AddButton("Отправить и разрешить",()=>
            {
                var values=readers.ToDictionary(x=>x.Key,x=>x.Value());
                if (required.Any(k=>!values.TryGetValue(k,out var value) || value is null)) throw new InvalidOperationException("Заполните обязательные поля.");
                return new { action="accept",content=values.Where(x=>x.Value is not null).ToDictionary(x=>x.Key,x=>x.Value),_meta=(object?)null };
            });
        }
        AddButton("Отказать",()=>new { action="decline",content=(object?)null,_meta=(object?)null });
        AddButton("Отменить",()=>new { action="cancel",content=(object?)null,_meta=(object?)null });
    }
}

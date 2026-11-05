using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace NudgeJudge.Tray;

/// <summary>
/// The tray icon on Linux: a StatusNotifierItem with a <c>com.canonical.dbusmenu</c> menu, spoken directly over the
/// session bus. KDE, Cinnamon, XFCE and others show these natively; GNOME with the AppIndicator extension.
/// </summary>
internal sealed class LinuxTray : ITrayIcon
{
    private const string ItemPath = "/StatusNotifierItem";
    private const string MenuPath = "/MenuBar";
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    private enum Item { Root = 0, Open, Separator1, Routing, Rules, Activity, Separator2, Quit }

    private static readonly Item[] MenuOrder =
        [Item.Open, Item.Separator1, Item.Routing, Item.Rules, Item.Activity, Item.Separator2, Item.Quit];

    private readonly TrayMenu _menu;
    private readonly string _address;
    private readonly string _serviceName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
    private readonly List<(int Width, int Height, byte[] Argb)> _pixmaps = [];
    private DBusConnection? _connection;
    private uint _revision = 1;
    private bool _routingShown;

    private LinuxTray(TrayMenu menu, string address)
    {
        _menu = menu;
        _address = address;
    }

    public static LinuxTray? TryCreate(TrayMenu menu) =>
        DBusAddress.Session is { Length: > 0 } address ? new LinuxTray(menu, address) : null;

    public void Run(CancellationToken stop)
    {
        _ = StartAsync();
        stop.WaitHandle.WaitOne();
    }

    private async Task StartAsync()
    {
        try
        {
            foreach (var size in new[] { 16, 32, 48 })
            {
                var (actual, png) = TrayIconImage.Best(size);
                if (actual != size) continue;
                // An image the small decoder can't read (re-saved as a palette PNG, say) costs that size, not the tray.
                try
                {
                    _pixmaps.Add(TrayIconImage.DecodeToArgb(png));
                }
                catch (Exception e) when (e is NotSupportedException or InvalidDataException or ArgumentException or EndOfStreamException)
                {
                    Trace.TraceWarning($"NudgeJudge: tray icon size {size} couldn't be read: {e.Message}");
                }
            }
            _routingShown = _menu.RoutingEnabled();

            var connection = new DBusConnection(_address);
            await connection.ConnectAsync();
            connection.AddMethodHandler(new Handler(ItemPath, HandleItem));
            connection.AddMethodHandler(new Handler(MenuPath, HandleMenu));
            await connection.RequestNameAsync(_serviceName, RequestNameOptions.Default);
            _connection = connection;

            // The watcher lives in the panel: register again whenever one (re)appears, e.g. after plasmashell restarts.
            await connection.WatchSignalAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged",
                static (Message m, object? _) =>
                {
                    var reader = m.GetBodyReader();
                    return (Name: reader.ReadString(), Old: reader.ReadString(), New: reader.ReadString());
                },
                (Notification<(string Name, string Old, string New)> n) =>
                {
                    if (n.HasValue && n.Value.Name == WatcherName && n.Value.New.Length > 0) _ = RegisterAsync();
                },
                ObserverFlags.None, false, null);
            await RegisterAsync();
        }
        catch (Exception e)
        {
            Trace.TraceWarning($"NudgeJudge: the tray icon isn't available: {e.Message}");
        }
    }

    private async Task RegisterAsync()
    {
        if (_connection is not { } connection) return;
        try
        {
            MessageBuffer message;
            var writer = connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(WatcherName, "/StatusNotifierWatcher", WatcherName, "RegisterStatusNotifierItem", "s",
                    MessageFlags.None);
                writer.WriteString(_serviceName);
                message = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
            await connection.CallMethodAsync(message);
        }
        catch (DBusErrorReplyException e)
        {
            // No panel with a tray right now (GNOME without the extension); registers if one turns up.
            Trace.TraceWarning($"NudgeJudge: no tray to show the icon in yet: {e.ErrorMessage}");
        }
    }

    public void Invalidate()
    {
        var routing = _menu.RoutingEnabled();
        if (routing == _routingShown) return;
        _routingShown = routing;
        SendLayoutUpdated();
    }

    private void SendLayoutUpdated()
    {
        if (_connection is not { } connection) return;
        var revision = Interlocked.Increment(ref _revision);
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null, MenuPath, "com.canonical.dbusmenu", "LayoutUpdated", "ui");
            writer.WriteUInt32(revision);
            writer.WriteInt32(0);
            connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    public void Dispose() => _connection?.Dispose();

    // ---- org.kde.StatusNotifierItem ----

    private void HandleItem(MethodContext context)
    {
        var request = context.Request;
        switch (request.InterfaceAsString, request.MemberAsString)
        {
            case ("org.kde.StatusNotifierItem", "Activate"):
                _menu.Open();
                ReplyEmpty(context);
                return;
            case ("org.kde.StatusNotifierItem", "SecondaryActivate" or "ContextMenu" or "Scroll" or "ProvideXdgActivationToken"):
                ReplyEmpty(context);
                return;
            case ("org.freedesktop.DBus.Properties", "Get"):
            {
                var reader = request.GetBodyReader();
                reader.ReadString();
                var name = reader.ReadString();
                if (Array.IndexOf(ItemProperties, name) < 0)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                    return;
                }
                Reply(context, "v", (ref MessageWriter w) => WriteItemProperty(ref w, name));
                return;
            }
            case ("org.freedesktop.DBus.Properties", "GetAll"):
            {
                Reply(context, "a{sv}", (ref MessageWriter w) =>
                {
                    var dict = w.WriteDictionaryStart();
                    foreach (var name in ItemProperties)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(name);
                        WriteItemProperty(ref w, name);
                    }
                    w.WriteDictionaryEnd(dict);
                });
                return;
            }
            case ("org.freedesktop.DBus.Introspectable", "Introspect"):
                context.ReplyIntrospectXml([ItemXml], Array.Empty<string>());
                return;
        }
        context.ReplyUnknownMethodError();
    }

    private static readonly string[] ItemProperties =
        ["Category", "Id", "Title", "Status", "WindowId", "IconName", "IconPixmap", "OverlayIconName", "AttentionIconName", "ToolTip", "ItemIsMenu", "Menu"];

    private void WriteItemProperty(ref MessageWriter writer, string name)
    {
        switch (name)
        {
            case "Category": writer.WriteVariantString("ApplicationStatus"); break;
            case "Id": writer.WriteVariantString("nudgejudge"); break;
            case "Title": writer.WriteVariantString("NudgeJudge"); break;
            case "Status": writer.WriteVariantString("Active"); break;
            case "WindowId": writer.WriteVariantInt32(0); break;
            // No pixmap could be read: fall back to the theme's browser icon rather than show nothing.
            case "IconName": writer.WriteVariantString(_pixmaps.Count == 0 ? "web-browser" : ""); break;
            case "OverlayIconName" or "AttentionIconName": writer.WriteVariantString(""); break;
            case "ItemIsMenu": writer.WriteVariantBool(false); break;
            case "Menu": writer.WriteVariantObjectPath(MenuPath); break;
            case "IconPixmap":
                writer.WriteSignature("a(iiay)");
                WritePixmaps(ref writer);
                break;
            case "ToolTip":
                writer.WriteSignature("(sa(iiay)ss)");
                writer.WriteStructureStart();
                writer.WriteString("");
                WritePixmaps(ref writer);
                writer.WriteString("NudgeJudge");
                writer.WriteString("");
                break;
        }
    }

    private void WritePixmaps(ref MessageWriter writer)
    {
        var array = writer.WriteArrayStart(DBusType.Struct);
        foreach (var (width, height, argb) in _pixmaps)
        {
            writer.WriteStructureStart();
            writer.WriteInt32(width);
            writer.WriteInt32(height);
            writer.WriteArray(argb);
        }
        writer.WriteArrayEnd(array);
    }

    // ---- com.canonical.dbusmenu ----

    private void HandleMenu(MethodContext context)
    {
        var request = context.Request;
        switch (request.InterfaceAsString, request.MemberAsString)
        {
            case ("com.canonical.dbusmenu", "GetLayout"):
            {
                var reader = request.GetBodyReader();
                var parent = reader.ReadInt32();
                Reply(context, "u(ia{sv}av)", (ref MessageWriter w) =>
                {
                    w.WriteUInt32(Volatile.Read(ref _revision));
                    if (parent == (int)Item.Root) WriteLayout(ref w);
                    else WriteNode(ref w, (Item)parent);
                });
                return;
            }
            case ("com.canonical.dbusmenu", "GetGroupProperties"):
            {
                var ids = request.GetBodyReader().ReadArrayOfInt32();
                Reply(context, "a(ia{sv})", (ref MessageWriter w) =>
                {
                    var array = w.WriteArrayStart(DBusType.Struct);
                    foreach (var id in ids)
                    {
                        if (!Enum.IsDefined((Item)id)) continue;
                        w.WriteStructureStart();
                        w.WriteInt32(id);
                        WriteItemProperties(ref w, (Item)id);
                    }
                    w.WriteArrayEnd(array);
                });
                return;
            }
            case ("com.canonical.dbusmenu", "GetProperty"):
                context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Use GetGroupProperties.");
                return;
            case ("com.canonical.dbusmenu", "Event"):
            {
                var reader = request.GetBodyReader();
                var id = (Item)reader.ReadInt32();
                var kind = reader.ReadString();
                if (kind == "clicked") Click(id);
                ReplyEmpty(context);
                return;
            }
            case ("com.canonical.dbusmenu", "EventGroup"):
            {
                var reader = request.GetBodyReader();
                var end = reader.ReadArrayStart(DBusType.Struct);
                while (reader.HasNext(end))
                {
                    reader.AlignStruct();
                    var id = (Item)reader.ReadInt32();
                    var kind = reader.ReadString();
                    reader.ReadVariantValue();
                    reader.ReadUInt32();
                    if (kind == "clicked") Click(id);
                }
                Reply(context, "ai", (ref MessageWriter w) => w.WriteArray(Array.Empty<int>()));
                return;
            }
            case ("com.canonical.dbusmenu", "AboutToShow"):
            {
                // The menu is opening: bring the check mark up to date first.
                var routing = _menu.RoutingEnabled();
                var stale = routing != _routingShown;
                _routingShown = routing;
                if (stale) Interlocked.Increment(ref _revision);
                Reply(context, "b", (ref MessageWriter w) => w.WriteBool(stale));
                return;
            }
            case ("com.canonical.dbusmenu", "AboutToShowGroup"):
            {
                Reply(context, "aiai", (ref MessageWriter w) =>
                {
                    w.WriteArray(Array.Empty<int>());
                    w.WriteArray(Array.Empty<int>());
                });
                return;
            }
            case ("org.freedesktop.DBus.Properties", "Get"):
            {
                var reader = request.GetBodyReader();
                reader.ReadString();
                var name = reader.ReadString();
                if (Array.IndexOf(MenuProperties, name) < 0)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", name);
                    return;
                }
                Reply(context, "v", (ref MessageWriter w) => WriteMenuProperty(ref w, name));
                return;
            }
            case ("org.freedesktop.DBus.Properties", "GetAll"):
            {
                Reply(context, "a{sv}", (ref MessageWriter w) =>
                {
                    var dict = w.WriteDictionaryStart();
                    foreach (var name in MenuProperties)
                    {
                        w.WriteDictionaryEntryStart();
                        w.WriteString(name);
                        WriteMenuProperty(ref w, name);
                    }
                    w.WriteDictionaryEnd(dict);
                });
                return;
            }
            case ("org.freedesktop.DBus.Introspectable", "Introspect"):
                context.ReplyIntrospectXml([MenuXml], Array.Empty<string>());
                return;
        }
        context.ReplyUnknownMethodError();
    }

    private static readonly string[] MenuProperties = ["Version", "TextDirection", "Status"];

    private static void WriteMenuProperty(ref MessageWriter writer, string name)
    {
        switch (name)
        {
            case "Version": writer.WriteVariantUInt32(3); break;
            case "TextDirection": writer.WriteVariantString("ltr"); break;
            case "Status": writer.WriteVariantString("normal"); break;
        }
    }

    private void WriteLayout(ref MessageWriter writer)
    {
        writer.WriteStructureStart();
        writer.WriteInt32((int)Item.Root);
        var props = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("children-display");
        writer.WriteVariantString("submenu");
        writer.WriteDictionaryEnd(props);
        var children = writer.WriteArrayStart(DBusType.Variant);
        foreach (var item in MenuOrder)
        {
            writer.WriteSignature("(ia{sv}av)");
            WriteNode(ref writer, item);
        }
        writer.WriteArrayEnd(children);
    }

    private void WriteNode(ref MessageWriter writer, Item item)
    {
        writer.WriteStructureStart();
        writer.WriteInt32((int)item);
        WriteItemProperties(ref writer, item);
        var none = writer.WriteArrayStart(DBusType.Variant);
        writer.WriteArrayEnd(none);
    }

    private void WriteItemProperties(ref MessageWriter writer, Item item)
    {
        var dict = writer.WriteDictionaryStart();
        void Entry(ref MessageWriter w, string key) { w.WriteDictionaryEntryStart(); w.WriteString(key); }

        if (item is Item.Separator1 or Item.Separator2)
        {
            Entry(ref writer, "type");
            writer.WriteVariantString("separator");
        }
        else
        {
            Entry(ref writer, "label");
            writer.WriteVariantString(item switch
            {
                Item.Open => "Open NudgeJudge",
                Item.Routing => "Route links by rules",
                Item.Rules => "Rules…",
                Item.Activity => "Recent activity…",
                Item.Quit => "Quit NudgeJudge",
                _ => "",
            });
            if (item == Item.Routing)
            {
                Entry(ref writer, "toggle-type");
                writer.WriteVariantString("checkmark");
                Entry(ref writer, "toggle-state");
                writer.WriteVariantInt32(_routingShown ? 1 : 0);
            }
        }
        writer.WriteDictionaryEnd(dict);
    }

    private void Click(Item item)
    {
        switch (item)
        {
            case Item.Open: _menu.Open(); break;
            case Item.Routing: _menu.ToggleRouting(); break;
            case Item.Rules: _menu.OpenRules(); break;
            case Item.Activity: _menu.OpenActivity(); break;
            case Item.Quit: _menu.Quit(); break;
        }
    }

    private delegate void BodyWriter(ref MessageWriter writer);

    private static void Reply(MethodContext context, string? signature, BodyWriter body)
    {
        if (context.NoReplyExpected) return;
        var writer = context.CreateReplyWriter(signature!);
        try
        {
            body(ref writer);
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void ReplyEmpty(MethodContext context) => Reply(context, null, static (ref MessageWriter _) => { });

    private static readonly ReadOnlyMemory<byte> ItemXml = """
        <interface name="org.kde.StatusNotifierItem">
          <method name="Activate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
          <method name="SecondaryActivate"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
          <method name="ContextMenu"><arg type="i" direction="in"/><arg type="i" direction="in"/></method>
          <method name="Scroll"><arg type="i" direction="in"/><arg type="s" direction="in"/></method>
          <property name="Category" type="s" access="read"/>
          <property name="Id" type="s" access="read"/>
          <property name="Title" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="WindowId" type="i" access="read"/>
          <property name="IconName" type="s" access="read"/>
          <property name="IconPixmap" type="a(iiay)" access="read"/>
          <property name="ToolTip" type="(sa(iiay)ss)" access="read"/>
          <property name="ItemIsMenu" type="b" access="read"/>
          <property name="Menu" type="o" access="read"/>
        </interface>
        """u8.ToArray();

    private static readonly ReadOnlyMemory<byte> MenuXml = """
        <interface name="com.canonical.dbusmenu">
          <method name="GetLayout"><arg type="i" direction="in"/><arg type="i" direction="in"/><arg type="as" direction="in"/><arg type="u" direction="out"/><arg type="(ia{sv}av)" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" direction="in"/><arg type="as" direction="in"/><arg type="a(ia{sv})" direction="out"/></method>
          <method name="Event"><arg type="i" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="in"/><arg type="u" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" direction="in"/><arg type="ai" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" direction="in"/><arg type="b" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" direction="in"/><arg type="ai" direction="out"/><arg type="ai" direction="out"/></method>
          <signal name="LayoutUpdated"><arg type="u"/><arg type="i"/></signal>
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
        </interface>
        """u8.ToArray();

    private sealed class Handler(string path, Action<MethodContext> handle) : IPathMethodHandler
    {
        public string Path => path;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            handle(context);
            return ValueTask.CompletedTask;
        }
    }
}

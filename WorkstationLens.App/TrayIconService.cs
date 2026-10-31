using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Hosting;

namespace WorkstationLens.App;

/// <summary>Optional Windows notification-area controls; no visible console or form.</summary>
internal sealed class TrayIconService(IHostApplicationLifetime lifetime, string dashboardUrl) : IHostedService
{
    private Thread? _thread;
    private ApplicationContext? _context;
    private NotifyIcon? _icon;
    private Control? _dispatcher;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Environment.UserInteractive) return Task.CompletedTask;
        _thread = new Thread(() =>
        {
            try
            {
                using var context = new ApplicationContext();
                _context = context;
                // Dedicated invisible window is a thread-safe dispatcher for
                // shutting down the WinForms message loop from the ASP.NET host.
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                _dispatcher = dispatcher;
                using var menu = new ContextMenuStrip();
                menu.Items.Add("Open Dashboard", null, (_,_) => Open());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit Workstation Lens", null, (_,_) => lifetime.StopApplication());
                using var icon = new NotifyIcon
                {
                    Text = "Workstation Lens — recording",
                    Icon = SystemIcons.Application,
                    ContextMenuStrip = menu,
                    Visible = true
                };
                _icon = icon;
                icon.DoubleClick += (_,_) => Open();
                Application.Run(context);
                icon.Visible = false;
            }
            catch (Exception)
            {
                // Headless sessions or disabled desktop interaction must not
                // prevent the lightweight Collector from running.
            }
            finally { _icon = null; _dispatcher = null; _context = null; }
        }) { IsBackground = true, Name = "Workstation Lens tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return Task.CompletedTask;
    }

    private void Open()
    {
        try { Process.Start(new ProcessStartInfo(dashboardUrl){ UseShellExecute = true }); }
        catch { /* The collector does not depend on the default browser. */ }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is not null && dispatcher.IsHandleCreated && !dispatcher.IsDisposed)
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_icon is not null) _icon.Visible = false;
                    _context?.ExitThread();
                }));
            }
        }
        catch (Exception) { }
        return Task.CompletedTask;
    }
}

using System.Windows.Threading;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Notch.Models;

namespace Notch.Services;

/// <summary>
/// Neue Gmail-Mails per IMAP IDLE: Pop-up pro Mail (nur Kategorie "Primär"), Ablage bis gelesen.
/// Login mit App-Passwort (DPAPI-verschlüsselt, siehe <see cref="GmailCredentials"/>).
/// Beim Start wird nur der aktuelle Stand gemerkt, alte Mails lösen nichts aus.
/// </summary>
public sealed class GmailService : IDisposable
{
    private const string Query = "category:primary";
    private static readonly TimeSpan IdleSlice = TimeSpan.FromMinutes(9);   // Gmail trennt idle-Verbindungen nach ~10 min

    private readonly EventResolver _resolver;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public GmailService(EventResolver resolver, Dispatcher dispatcher)
    {
        _resolver = resolver;
        _dispatcher = dispatcher;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public void Start()
    {
        Stop();
        var login = GmailCredentials.Load();
        if (login is null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => RunAsync(login, token), token);
        Log.Info($"Gmail gestartet: {login.User}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _loop = null;
    }

    private async Task RunAsync(GmailCredentials.Login login, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        uint lastUid = 0;
        var primed = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new ImapClient();
                await client.ConnectAsync("imap.gmail.com", 993, SecureSocketOptions.SslOnConnect, ct);
                await client.AuthenticateAsync(login.User, login.Password, ct);
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                delay = TimeSpan.FromSeconds(5);

                if (!primed)
                {
                    lastUid = inbox.UidNext is { } next && next.Id > 0 ? next.Id - 1 : 0;
                    primed = true;
                }

                while (!ct.IsCancellationRequested && client.IsConnected)
                {
                    lastUid = await FetchNewAsync(inbox, lastUid, ct);

                    using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    done.CancelAfter(IdleSlice);
                    void OnCount(object? s, EventArgs e) => done.Cancel();
                    inbox.CountChanged += OnCount;
                    try { await client.IdleAsync(done.Token, ct); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                    finally { inbox.CountChanged -= OnCount; }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (AuthenticationException ex)
            {
                Log.Error("Gmail-Anmeldung abgelehnt (App-Passwort prüfen)", ex);
                Post(new NotchEvent("gmail", EventKind.Error, "Gmail-Anmeldung fehlgeschlagen",
                    "App-Passwort prüfen (Tray → Gmail verbinden)", Id: "auth", TtlSeconds: 10));
                return;   // nicht weiter probieren, sonst sperrt Google das Konto zeitweise
            }
            catch (Exception ex)
            {
                Log.Error($"Gmail-Verbindung unterbrochen, neuer Versuch in {delay.TotalSeconds:0} s", ex);
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
        }
    }

    private async Task<uint> FetchNewAsync(IMailFolder inbox, uint lastUid, CancellationToken ct)
    {
        var range = new UniqueIdRange(new UniqueId(lastUid + 1), UniqueId.MaxValue);
        var query = SearchQuery.Uids(range).And(SearchQuery.GMailRaw(Query));
        var uids = await inbox.SearchAsync(query, ct);
        var highest = lastUid;

        foreach (var uid in uids.Where(u => u.Id > lastUid).OrderBy(u => u.Id))
        {
            highest = Math.Max(highest, uid.Id);
            try
            {
                var msg = await inbox.GetMessageAsync(uid, ct);
                var from = msg.From.Mailboxes.FirstOrDefault();
                var sender = !string.IsNullOrWhiteSpace(from?.Name) ? from!.Name : from?.Address ?? "Unbekannt";
                var subject = string.IsNullOrWhiteSpace(msg.Subject) ? "(ohne Betreff)" : msg.Subject;
                Post(new NotchEvent("gmail", EventKind.Message, sender, subject,
                    TtlSeconds: 6, Id: uid.Id.ToString(), Open: "https://mail.google.com/mail/u/0/#inbox", Glyph: ""));
            }
            catch (Exception ex)
            {
                Log.Error($"Gmail-Mail {uid.Id} nicht lesbar", ex);
            }
        }

        // UIDs anderer Kategorien nicht erneut prüfen
        if (inbox.UidNext is { } n && n.Id > 0) highest = Math.Max(highest, n.Id - 1);
        return highest;
    }

    private void Post(NotchEvent e) => _dispatcher.BeginInvoke(new Action(() => _resolver.Submit(e)));

    public void Dispose() => Stop();
}

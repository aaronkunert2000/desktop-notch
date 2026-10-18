using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Notch.Views;

/// <summary>Kleiner Dialog für Gmail-Adresse und App-Passwort (Eingabe maskiert, wird nie geloggt).</summary>
public sealed class GmailSetupDialog : Forms.Form
{
    private readonly Forms.TextBox _user = new() { Width = 300 };
    private readonly Forms.TextBox _pass = new() { Width = 300, UseSystemPasswordChar = true };

    public string User => _user.Text.Trim();
    public string Password => _pass.Text;

    public GmailSetupDialog(string? currentUser)
    {
        Text = "Gmail verbinden";
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        ClientSize = new Drawing.Size(340, 190);
        Font = new Drawing.Font("Segoe UI", 9f);

        var info = new Forms.Label
        {
            Left = 20, Top = 12, Width = 300, Height = 44,
            Text = "App-Passwort erstellen unter myaccount.google.com/apppasswords " +
                   "(2-Faktor muss aktiv sein). Normales Passwort funktioniert nicht.",
        };
        _user.Left = 20; _user.Top = 78; _user.Text = currentUser ?? "";
        _pass.Left = 20; _pass.Top = 118;
        var ok = new Forms.Button { Text = "Verbinden", Left = 160, Top = 152, Width = 80, DialogResult = Forms.DialogResult.OK };
        var cancel = new Forms.Button { Text = "Abbrechen", Left = 245, Top = 152, Width = 80, DialogResult = Forms.DialogResult.Cancel };
        var l1 = new Forms.Label { Left = 20, Top = 62, Width = 200, Text = "Gmail-Adresse" };
        var l2 = new Forms.Label { Left = 20, Top = 102, Width = 200, Text = "App-Passwort (16 Zeichen)" };

        Controls.AddRange(new Forms.Control[] { info, l1, _user, l2, _pass, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

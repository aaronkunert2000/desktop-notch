using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Notch.Views;

/// <summary>Kleiner Dialog für ein Geheimnis (Eingabe maskiert, wird nie geloggt).</summary>
public sealed class SecretPromptDialog : Forms.Form
{
    private readonly Forms.TextBox _secret = new() { Width = 300, UseSystemPasswordChar = true };

    public string Secret => _secret.Text.Trim();

    public SecretPromptDialog(string title, string info, string label)
    {
        Text = title;
        FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        ClientSize = new Drawing.Size(340, 190);
        Font = new Drawing.Font("Segoe UI", 9f);

        var infoLabel = new Forms.Label { Left = 20, Top = 12, Width = 300, Height = 80, Text = info };
        var fieldLabel = new Forms.Label { Left = 20, Top = 100, Width = 300, Text = label };
        _secret.Left = 20; _secret.Top = 120;
        var ok = new Forms.Button { Text = "Speichern", Left = 160, Top = 152, Width = 80, DialogResult = Forms.DialogResult.OK };
        var cancel = new Forms.Button { Text = "Abbrechen", Left = 245, Top = 152, Width = 80, DialogResult = Forms.DialogResult.Cancel };

        Controls.AddRange(new Forms.Control[] { infoLabel, fieldLabel, _secret, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

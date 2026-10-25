namespace Fecho.Agent;

/// <summary>Pede a senha do Windows uma vez, para guardar protegida por DPAPI.</summary>
public sealed class PasswordForm : Form
{
    private readonly TextBox _user;
    private readonly TextBox _password;
    private readonly Label _status;

    public PasswordForm()
    {
        Text = "Ativar desbloqueio pelo iPhone — Fecho";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 320);
        Font = new Font("Segoe UI", 9.5f);

        var intro = new Label
        {
            Text = "Digite a senha da sua conta do Windows. Ela fica guardada só neste PC, "
                 + "cifrada pelo Windows (DPAPI), e nunca é enviada ao iPhone.\n\n"
                 + "O desbloqueio pelo iPhone exige o seu Face ID a cada vez.",
            Location = new Point(20, 16),
            Size = new Size(400, 70),
            ForeColor = Color.FromArgb(60, 60, 67),
        };

        var userLabel = new Label { Text = "Usuário", Location = new Point(20, 96), AutoSize = true };
        _user = new TextBox { Location = new Point(20, 116), Size = new Size(400, 26), Text = Environment.UserName };

        var pwdLabel = new Label { Text = "Senha do Windows", Location = new Point(20, 150), AutoSize = true };
        _password = new TextBox { Location = new Point(20, 170), Size = new Size(400, 26), UseSystemPasswordChar = true };

        _status = new Label { Location = new Point(20, 204), Size = new Size(400, 36), ForeColor = Color.Firebrick };

        var save = new Button { Text = "Verificar e salvar", Location = new Point(236, 264), Size = new Size(184, 36) };
        save.Click += OnSave;
        var cancel = new Button { Text = "Cancelar", Location = new Point(140, 264), Size = new Size(90, 36), DialogResult = DialogResult.Cancel };

        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { intro, userLabel, _user, pwdLabel, _password, _status, save, cancel });
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var user = _user.Text.Trim();
        var pwd = _password.Text;
        if (user.Length == 0 || pwd.Length == 0)
        {
            _status.Text = "Preencha usuário e senha.";
            return;
        }

        var domain = Environment.UserDomainName;
        if (UnlockSecrets.SetPassword(domain, user, pwd))
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        var answer = MessageBox.Show(this,
            "O Windows não confirmou essa senha. Isso é esperado para contas Microsoft, "
            + "onde a verificação local falha mesmo com a senha certa.\n\nSalvar assim mesmo?",
            "Fecho", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
        {
            UnlockSecrets.SetPassword(domain, user, pwd, verify: false);
            DialogResult = DialogResult.OK;
            Close();
        }
        else
        {
            _status.Text = "Senha não confirmada.";
        }
    }
}

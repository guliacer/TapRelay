using System;
using System.Drawing;
using System.Windows.Forms;

namespace TapRelay
{
    internal enum UpdateDialogResult
    {
        None,
        Update,
        SkipOnce,
        Never
    }

    internal sealed class UpdateDialog : Form
    {
        internal UpdateDialog(UpdateReleaseInfo release, string currentVersion)
        {
            Text = "TapRelay 更新提示";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(520, 380);
            AutoScaleMode = AutoScaleMode.Dpi;

            var title = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 34,
                Font = new Font(Font, FontStyle.Bold),
                Text = string.IsNullOrWhiteSpace(release.Name) ? "发现新版本" : release.Name,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var version = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 28,
                ForeColor = SystemColors.GrayText,
                Text = string.Format("当前版本 {0}  ·  最新版本 {1}", currentVersion, release.DisplayVersion),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var body = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                DetectUrls = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                Text = string.IsNullOrWhiteSpace(release.Body) ? "本次更新未提供详细说明。" : release.Body.Trim(),
                BackColor = SystemColors.Window,
                Font = new Font("Segoe UI", 9.5f)
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 52,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 12, 0, 0)
            };

            var update = CreateButton("去更新", 92);
            var skip = CreateButton("跳过本次", 92);
            var never = CreateButton("永久跳过", 92);
            update.Click += (_, __) => Complete(UpdateDialogResult.Update);
            skip.Click += (_, __) => Complete(UpdateDialogResult.SkipOnce);
            never.Click += (_, __) => Complete(UpdateDialogResult.Never);
            buttons.Controls.Add(update);
            buttons.Controls.Add(skip);
            buttons.Controls.Add(never);

            var content = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 14, 16, 0)
            };
            content.Controls.Add(body);
            content.Controls.Add(version);
            content.Controls.Add(title);

            Controls.Add(content);
            Controls.Add(buttons);
            AcceptButton = update;
            CancelButton = null;
            FormClosed += (_, __) =>
            {
                if (DialogResult == DialogResult.None)
                {
                    Result = UpdateDialogResult.None;
                }
            };
        }

        internal UpdateDialogResult Result { get; private set; }

        private static Button CreateButton(string text, int width)
        {
            return new Button
            {
                AutoSize = false,
                Width = width,
                Height = 30,
                Text = text,
                UseVisualStyleBackColor = true,
                Margin = new Padding(6, 0, 0, 0)
            };
        }

        private void Complete(UpdateDialogResult result)
        {
            Result = result;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

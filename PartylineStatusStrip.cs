using System;
using System.Drawing;
using System.Windows.Forms;

namespace Partyline
{
    /// <summary>
    /// Compact embedded control showing co-host count and a button to open the full mixer.
    /// Registered via App.RegisterUserControl at UserControlLocation.BelowTrackList.
    /// </summary>
    public class PartylineStatusStrip : UserControl
    {
        private Label _statusLabel;
        private Label _countLabel;
        private Button _mixerBtn;
        private Button _muteAllBtn;
        private CoHostManager _manager;
        private Timer _refreshTimer;

        public PartylineStatusStrip()
        {
            InitializeComponent();
        }

        public void SetManager(CoHostManager manager)
        {
            _manager = manager;
        }

        private void InitializeComponent()
        {
            Height = 36;
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(30, 30, 50);
            Padding = new Padding(6, 4, 6, 4);

            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false
            };

            _statusLabel = new Label
            {
                Text = "🎙️ Partyline",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 5, 10, 0)
            };

            _countLabel = new Label
            {
                Text = "0 connected",
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = true,
                Margin = new Padding(0, 6, 15, 0)
            };

            _muteAllBtn = new Button
            {
                Text = "Mute All",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(100, 116, 139),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f),
                Size = new Size(65, 26),
                Margin = new Padding(0, 2, 6, 0),
                Cursor = Cursors.Hand
            };
            _muteAllBtn.FlatAppearance.BorderSize = 0;
            _muteAllBtn.Click += OnMuteAll;

            _mixerBtn = new Button
            {
                Text = "Show Partyline Mixer",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                Size = new Size(140, 26),
                Margin = new Padding(0, 2, 0, 0),
                Cursor = Cursors.Hand
            };
            _mixerBtn.FlatAppearance.BorderSize = 0;
            _mixerBtn.Click += OnShowMixer;

            layout.Controls.Add(_statusLabel);
            layout.Controls.Add(_countLabel);
            layout.Controls.Add(_muteAllBtn);
            layout.Controls.Add(_mixerBtn);
            Controls.Add(layout);

            // Refresh timer
            _refreshTimer = new Timer { Interval = 1000 };
            _refreshTimer.Tick += OnRefresh;
            _refreshTimer.Start();
        }

        private void OnRefresh(object sender, EventArgs e)
        {
            if (_manager == null) return;

            var count = _manager.GetSessionCount();
            _countLabel.Text = count == 1 ? "1 connected" : $"{count} connected";
            _countLabel.ForeColor = count > 0
                ? Color.FromArgb(34, 197, 94)   // green
                : Color.FromArgb(148, 163, 184); // gray
        }

        private void OnMuteAll(object sender, EventArgs e)
        {
            _manager?.MuteAll();
            _muteAllBtn.Text = "Muted ✓";
            var timer = new Timer { Interval = 2000 };
            timer.Tick += (s, ev) => { _muteAllBtn.Text = "Mute All"; timer.Stop(); timer.Dispose(); };
            timer.Start();
        }

        private void OnShowMixer(object sender, EventArgs e)
        {
            PartylineMixerForm.ShowInstance(_manager);
        }

        protected override void Dispose(bool disposing)
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            base.Dispose(disposing);
        }
    }
}

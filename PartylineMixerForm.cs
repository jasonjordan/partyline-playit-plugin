using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Partyline
{
    /// <summary>
    /// Floating mixer window with per-co-host volume, mute, and kick controls.
    /// </summary>
    public class PartylineMixerForm : Form
    {
        private static PartylineMixerForm _instance;
        private CoHostManager _manager;
        private FlowLayoutPanel _channelPanel;
        private Label _emptyLabel;
        private Timer _refreshTimer;
        private Dictionary<string, ChannelStrip> _strips = new();

        public static void ShowInstance(CoHostManager manager)
        {
            if (_instance == null || _instance.IsDisposed)
            {
                _instance = new PartylineMixerForm(manager);
            }
            _instance.Show();
            _instance.BringToFront();
        }

        private PartylineMixerForm(CoHostManager manager)
        {
            _manager = manager;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Text = "Partyline Mixer";
            Size = new Size(420, 350);
            MinimumSize = new Size(350, 250);
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 40);
            TopMost = true;

            // Header
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 40,
                BackColor = Color.FromArgb(30, 30, 55),
                Padding = new Padding(10, 8, 10, 8)
            };

            var titleLabel = new Label
            {
                Text = "🎙️ Connected Co-Hosts",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(10, 10)
            };
            header.Controls.Add(titleLabel);

            var disconnectAllBtn = new Button
            {
                Text = "Kick All",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(239, 68, 68),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f),
                Size = new Size(60, 24),
                Anchor = AnchorStyles.Right | AnchorStyles.Top,
                Cursor = Cursors.Hand
            };
            disconnectAllBtn.Location = new Point(header.Width - 80, 8);
            disconnectAllBtn.FlatAppearance.BorderSize = 0;
            disconnectAllBtn.Click += (s, e) => _manager?.DisconnectAll();
            header.Controls.Add(disconnectAllBtn);

            // Channel strips container
            _channelPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10)
            };

            _emptyLabel = new Label
            {
                Text = "No co-hosts connected.\n\nShare the link from the Partyline menu\nto invite co-hosts.",
                ForeColor = Color.FromArgb(148, 163, 184),
                Font = new Font("Segoe UI", 9.5f),
                AutoSize = true,
                Margin = new Padding(10, 20, 0, 0),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _channelPanel.Controls.Add(_emptyLabel);

            Controls.Add(_channelPanel);
            Controls.Add(header);

            // Refresh
            _refreshTimer = new Timer { Interval = 500 };
            _refreshTimer.Tick += OnRefresh;
            _refreshTimer.Start();
        }

        private void OnRefresh(object sender, EventArgs e)
        {
            if (_manager == null) return;

            var sessions = _manager.GetSessions();

            // Remove disconnected strips
            var toRemove = new List<string>();
            foreach (var kvp in _strips)
            {
                if (!sessions.ContainsKey(kvp.Key))
                {
                    _channelPanel.Controls.Remove(kvp.Value);
                    kvp.Value.Dispose();
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (var key in toRemove) _strips.Remove(key);

            // Add new strips
            foreach (var kvp in sessions)
            {
                if (!_strips.ContainsKey(kvp.Key))
                {
                    var strip = new ChannelStrip(kvp.Key, kvp.Value, _manager);
                    _strips[kvp.Key] = strip;
                    _channelPanel.Controls.Add(strip);
                }
                else
                {
                    _strips[kvp.Key].UpdateLevel(kvp.Value.GetLevel());
                }
            }

            _emptyLabel.Visible = _strips.Count == 0;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Hide instead of close
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        protected override void Dispose(bool disposing)
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A single channel strip representing one co-host with volume, mute, and kick controls.
    /// </summary>
    internal class ChannelStrip : UserControl
    {
        private string _sessionId;
        private CoHostSession _session;
        private CoHostManager _manager;
        private TrackBar _volumeSlider;
        private Button _muteBtn;
        private Button _kickBtn;
        private ProgressBar _vuMeter;
        private Label _nameLabel;
        private bool _muted;

        public ChannelStrip(string sessionId, CoHostSession session, CoHostManager manager)
        {
            _sessionId = sessionId;
            _session = session;
            _manager = manager;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Size = new Size(380, 50);
            Margin = new Padding(0, 0, 0, 4);
            BackColor = Color.FromArgb(40, 40, 65);
            Padding = new Padding(8, 6, 8, 6);

            _nameLabel = new Label
            {
                Text = $"Co-Host ({_sessionId})",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location = new Point(8, 6),
                AutoSize = true
            };

            _vuMeter = new ProgressBar
            {
                Location = new Point(8, 30),
                Size = new Size(100, 10),
                Style = ProgressBarStyle.Continuous,
                Maximum = 100,
                Value = 0
            };

            _volumeSlider = new TrackBar
            {
                Location = new Point(115, 18),
                Size = new Size(120, 20),
                Minimum = 0,
                Maximum = 100,
                Value = 100,
                TickStyle = TickStyle.None
            };
            _volumeSlider.ValueChanged += (s, e) =>
            {
                _session?.SetVolume(_volumeSlider.Value / 100f);
            };

            _muteBtn = new Button
            {
                Text = "🔊",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(100, 116, 139),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f),
                Size = new Size(36, 30),
                Location = new Point(245, 8),
                Cursor = Cursors.Hand
            };
            _muteBtn.FlatAppearance.BorderSize = 0;
            _muteBtn.Click += OnToggleMute;

            _kickBtn = new Button
            {
                Text = "✕",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(239, 68, 68),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Size = new Size(30, 30),
                Location = new Point(288, 8),
                Cursor = Cursors.Hand
            };
            _kickBtn.FlatAppearance.BorderSize = 0;
            _kickBtn.Click += OnKick;

            Controls.Add(_nameLabel);
            Controls.Add(_vuMeter);
            Controls.Add(_volumeSlider);
            Controls.Add(_muteBtn);
            Controls.Add(_kickBtn);
        }

        public void UpdateLevel(float level)
        {
            int value = (int)(level * 100);
            if (value < 0) value = 0;
            if (value > 100) value = 100;
            if (_vuMeter.InvokeRequired)
                _vuMeter.Invoke(new Action(() => _vuMeter.Value = value));
            else
                _vuMeter.Value = value;
        }

        private void OnToggleMute(object sender, EventArgs e)
        {
            _muted = !_muted;
            _session?.SetMuted(_muted);
            _muteBtn.Text = _muted ? "🔇" : "🔊";
            _muteBtn.BackColor = _muted
                ? Color.FromArgb(239, 68, 68)
                : Color.FromArgb(100, 116, 139);
        }

        private void OnKick(object sender, EventArgs e)
        {
            _manager?.KickSession(_sessionId);
        }
    }
}

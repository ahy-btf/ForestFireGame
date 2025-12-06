using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ForestFireGame
{
    public partial class Form1 : Form
    {
        private class Player
        {
            public string Role { get; }
            public PictureBox Sprite { get; }
            public Keys Up { get; }
            public Keys Down { get; }
            public Keys Left { get; }
            public Keys Right { get; }

            public bool PressUp { get; set; }
            public bool PressDown { get; set; }
            public bool PressLeft { get; set; }
            public bool PressRight { get; set; }

            public int Speed { get; set; } = 4;

            public Player(string role, PictureBox sprite, Keys up, Keys down, Keys left, Keys right)
            {
                Role = role;
                Sprite = sprite;
                Up = up;
                Down = down;
                Left = left;
                Right = right;
            }
        }

        private const int Port = 9000;
        private readonly string assetRoot = Path.Combine(Application.StartupPath, "Resource");

        private Player firePlayer;
        private Player icePlayer;
        private PictureBox exitBox;
        private PictureBox redGemBox;
        private PictureBox blueGemBox;

        private bool redCollected;
        private bool blueCollected;

        private TcpListener listener;
        private TcpClient client;
        private StreamReader reader;
        private StreamWriter writer;
        private CancellationTokenSource networkCts;
        private bool isHost;

        public Form1()
        {
            InitializeComponent();
            DoubleBuffered = true;
            InitializeStage();
            lblStatus.Text = "状态：点击单机开始或建立联机";
        }

        private void InitializeStage()
        {
            gamePanel.Controls.Clear();
            gamePanel.BackgroundImage = LoadAsset("第一关地图.jpg");
            gamePanel.BackgroundImageLayout = ImageLayout.Stretch;

            // 玩家
            var fireBox = CreateSprite("火娃静.png", new Size(60, 60));
            var iceBox = CreateSprite("冰娃静.png", new Size(60, 60));
            firePlayer = new Player("FIRE", fireBox, Keys.Up, Keys.Down, Keys.Left, Keys.Right);
            icePlayer = new Player("ICE", iceBox, Keys.W, Keys.S, Keys.A, Keys.D);

            exitBox = CreateSprite("出口.jpg", new Size(90, 90));
            redGemBox = CreateSprite("红钻.jpg", new Size(36, 36));
            blueGemBox = CreateSprite("蓝钻.jpg", new Size(36, 36));

            gamePanel.Controls.Add(exitBox);
            gamePanel.Controls.Add(redGemBox);
            gamePanel.Controls.Add(blueGemBox);
            gamePanel.Controls.Add(firePlayer.Sprite);
            gamePanel.Controls.Add(icePlayer.Sprite);

            ResetObjects();
        }

        private PictureBox CreateSprite(string fileName, Size size)
        {
            var box = new PictureBox
            {
                Size = size,
                SizeMode = PictureBoxSizeMode.StretchImage,
                BackColor = Color.Transparent,
                Image = LoadAsset(fileName)
            };
            return box;
        }

        private Image LoadAsset(string fileName)
        {
            var path = Path.Combine(assetRoot, fileName);
            if (File.Exists(path))
            {
                return Image.FromFile(path);
            }

            Bitmap fallback = new Bitmap(40, 40);
            using (Graphics g = Graphics.FromImage(fallback))
            {
                g.Clear(Color.DarkGray);
                g.DrawString("缺图", new Font("Microsoft YaHei", 8), Brushes.Black, new PointF(2, 10));
            }
            return fallback;
        }

        private void ResetObjects()
        {
            redCollected = false;
            blueCollected = false;
            redGemBox.Visible = true;
            blueGemBox.Visible = true;

            firePlayer.Sprite.Location = new Point(40, gamePanel.Height - 110);
            icePlayer.Sprite.Location = new Point(140, gamePanel.Height - 110);
            exitBox.Location = new Point(gamePanel.Width - 120, 20);
            redGemBox.Location = new Point(gamePanel.Width / 2 - 100, gamePanel.Height / 2 + 20);
            blueGemBox.Location = new Point(gamePanel.Width / 2 + 80, gamePanel.Height / 2 + 20);

            lblStatus.Text = "状态：准备就绪，按键开始移动";
        }

        private void btnStartLocal_Click(object sender, EventArgs e)
        {
            DisconnectNetwork();
            InitializeStage();
            gameTimer.Start();
            lblStatus.Text = "状态：单机模式运行中";
        }

        private async void btnHost_Click(object sender, EventArgs e)
        {
            try
            {
                DisconnectNetwork();
                isHost = true;
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                lblStatus.Text = "状态：等待客户端加入…";
                client = await listener.AcceptTcpClientAsync();
                SetupNetworkStreams(client);
                lblStatus.Text = "状态：客户端已连接，控制火娃";
                InitializeStage();
                gameTimer.Start();
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"主机创建失败：{ex.Message}";
                DisconnectNetwork();
            }
        }

        private async void btnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                DisconnectNetwork();
                isHost = false;
                client = new TcpClient();
                await client.ConnectAsync(IPAddress.Parse(txtAddress.Text.Trim()), Port);
                SetupNetworkStreams(client);
                lblStatus.Text = "状态：已连接到主机，控制冰娃";
                InitializeStage();
                gameTimer.Start();
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"连接失败：{ex.Message}";
                DisconnectNetwork();
            }
        }

        private void SetupNetworkStreams(TcpClient tcpClient)
        {
            networkCts = new CancellationTokenSource();
            var stream = tcpClient.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8);
            writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            _ = Task.Run(() => ReceiveLoopAsync(networkCts.Token));
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    ApplyNetworkMessage(line);
                }
            }
            catch
            {
                // ignore exceptions when closing
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    BeginInvoke(new Action(() => lblStatus.Text = "连接已断开"));
                }
            }
        }

        private void ApplyNetworkMessage(string line)
        {
            var parts = line.Split('|');
            if (parts.Length != 3)
            {
                return;
            }

            string role = parts[0];
            if (!int.TryParse(parts[1], out int x) || !int.TryParse(parts[2], out int y))
            {
                return;
            }

            if (role == "FIRE")
            {
                BeginInvoke(new Action(() => firePlayer.Sprite.Location = ClampToPanel(new Point(x, y))));
            }
            else if (role == "ICE")
            {
                BeginInvoke(new Action(() => icePlayer.Sprite.Location = ClampToPanel(new Point(x, y))));
            }
        }

        private void gameTimer_Tick(object sender, EventArgs e)
        {
            UpdatePlayer(firePlayer, !IsConnected || isHost);
            UpdatePlayer(icePlayer, !IsConnected || !isHost);
            CheckPickups();
            CheckWin();
        }

        private void UpdatePlayer(Player player, bool allowControl)
        {
            if (!allowControl)
            {
                return;
            }

            var pos = player.Sprite.Location;
            if (player.PressUp)
            {
                pos.Y -= player.Speed;
            }
            if (player.PressDown)
            {
                pos.Y += player.Speed;
            }
            if (player.PressLeft)
            {
                pos.X -= player.Speed;
            }
            if (player.PressRight)
            {
                pos.X += player.Speed;
            }

            pos = ClampToPanel(pos);
            player.Sprite.Location = pos;
            SendNetworkState(player);
        }

        private Point ClampToPanel(Point pos)
        {
            int maxX = Math.Max(0, gamePanel.Width - firePlayer.Sprite.Width);
            int maxY = Math.Max(0, gamePanel.Height - firePlayer.Sprite.Height);
            pos.X = Math.Min(Math.Max(0, pos.X), maxX);
            pos.Y = Math.Min(Math.Max(0, pos.Y), maxY);
            return pos;
        }

        private void CheckPickups()
        {
            if (redGemBox.Visible && firePlayer.Sprite.Bounds.IntersectsWith(redGemBox.Bounds))
            {
                redGemBox.Visible = false;
                redCollected = true;
                lblStatus.Text = "已收集红钻，还差蓝钻";
            }
            if (redGemBox.Visible && icePlayer.Sprite.Bounds.IntersectsWith(redGemBox.Bounds))
            {
                redGemBox.Visible = false;
                redCollected = true;
                lblStatus.Text = "已收集红钻，还差蓝钻";
            }

            if (blueGemBox.Visible && firePlayer.Sprite.Bounds.IntersectsWith(blueGemBox.Bounds))
            {
                blueGemBox.Visible = false;
                blueCollected = true;
                lblStatus.Text = "已收集蓝钻，还差红钻";
            }
            if (blueGemBox.Visible && icePlayer.Sprite.Bounds.IntersectsWith(blueGemBox.Bounds))
            {
                blueGemBox.Visible = false;
                blueCollected = true;
                lblStatus.Text = "已收集蓝钻，还差红钻";
            }
        }

        private void CheckWin()
        {
            if (redCollected && blueCollected && firePlayer.Sprite.Bounds.IntersectsWith(exitBox.Bounds)
                && icePlayer.Sprite.Bounds.IntersectsWith(exitBox.Bounds))
            {
                gameTimer.Stop();
                lblStatus.Text = "通关成功！点击单机开始可以重新体验";
            }
        }

        private void SendNetworkState(Player player)
        {
            if (!IsConnected)
            {
                return;
            }

            try
            {
                writer.WriteLine($"{player.Role}|{player.Sprite.Location.X}|{player.Sprite.Location.Y}");
            }
            catch
            {
                // 网络异常时交给接收循环清理
            }
        }

        private bool IsConnected => client != null && client.Connected;

        private void Form1_KeyDown(object sender, KeyEventArgs e)
        {
            HandleKeyState(firePlayer, e.KeyCode, true);
            HandleKeyState(icePlayer, e.KeyCode, true);
        }

        private void Form1_KeyUp(object sender, KeyEventArgs e)
        {
            HandleKeyState(firePlayer, e.KeyCode, false);
            HandleKeyState(icePlayer, e.KeyCode, false);
        }

        private void HandleKeyState(Player player, Keys key, bool isPressed)
        {
            if (key == player.Up) player.PressUp = isPressed;
            if (key == player.Down) player.PressDown = isPressed;
            if (key == player.Left) player.PressLeft = isPressed;
            if (key == player.Right) player.PressRight = isPressed;
        }

        private void DisconnectNetwork()
        {
            try
            {
                networkCts?.Cancel();
                reader?.Dispose();
                writer?.Dispose();
                client?.Close();
                listener?.Stop();
            }
            catch
            {
                // ignore
            }
            finally
            {
                networkCts = null;
                reader = null;
                writer = null;
                client = null;
                listener = null;
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            DisconnectNetwork();
        }
    }
}

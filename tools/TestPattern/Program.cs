using System.Diagnostics;
using System.Drawing.Drawing2D;

// Animated capture target for stress tests. Draws a bouncing box, a frame counter, a millisecond clock
// and corner markers at ~60 fps, so capture fps, latency and coordinate accuracy can all be measured.
//
//   TestPattern [--size WxH] [--pos X,Y] [--title T] [--resize-every S] [--move-every S] [--close-after S]
//               [--minimize-at S --restore-at S] [--fps N]

var width = 1280; var height = 720; var x = 100; var y = 100;
var title = "WindowCast Test Pattern";
var resizeEvery = 0.0; var moveEvery = 0.0; var closeAfter = 0.0; var minimizeAt = 0.0; var restoreAt = 0.0;
var targetFps = 60;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--size": { var p = args[++i].Split('x'); width = int.Parse(p[0]); height = int.Parse(p[1]); break; }
        case "--pos": { var p = args[++i].Split(','); x = int.Parse(p[0]); y = int.Parse(p[1]); break; }
        case "--title": title = args[++i]; break;
        case "--resize-every": resizeEvery = double.Parse(args[++i]); break;
        case "--move-every": moveEvery = double.Parse(args[++i]); break;
        case "--close-after": closeAfter = double.Parse(args[++i]); break;
        case "--minimize-at": minimizeAt = double.Parse(args[++i]); break;
        case "--restore-at": restoreAt = double.Parse(args[++i]); break;
        case "--fps": targetFps = int.Parse(args[++i]); break;
    }
}

ApplicationConfiguration.Initialize();
Application.Run(new PatternForm(width, height, x, y, title, resizeEvery, moveEvery, closeAfter, minimizeAt, restoreAt, targetFps));

sealed class PatternForm : Form
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly double _resizeEvery, _moveEvery, _closeAfter, _minimizeAt, _restoreAt;
    private long _frame;
    private double _bx, _by, _vx = 420, _vy = 300;
    private double _last;
    private int _resizeStep, _moveStep;
    private double _nextResize, _nextMove;
    private bool _minimized, _restored;
    private readonly Font _big = new("Consolas", 40, FontStyle.Bold);
    private readonly Font _small = new("Consolas", 14);

    public PatternForm(int w, int h, int x, int y, string title, double resizeEvery, double moveEvery, double closeAfter, double minimizeAt, double restoreAt, int fps)
    {
        Text = title;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(x, y);
        ClientSize = new Size(w, h);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(13, 13, 26);
        _resizeEvery = resizeEvery; _moveEvery = moveEvery; _closeAfter = closeAfter; _minimizeAt = minimizeAt; _restoreAt = restoreAt;
        _nextResize = resizeEvery; _nextMove = moveEvery;
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, 1000 / fps) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var t = _clock.Elapsed.TotalSeconds;
        var dt = t - _last; _last = t;
        _bx += _vx * dt; _by += _vy * dt;
        var cw = ClientSize.Width; var ch = ClientSize.Height;
        if (_bx < 0) { _bx = 0; _vx = -_vx; } if (_bx > cw - 120) { _bx = cw - 120; _vx = -_vx; }
        if (_by < 0) { _by = 0; _vy = -_vy; } if (_by > ch - 120) { _by = ch - 120; _vy = -_vy; }

        if (_resizeEvery > 0 && t >= _nextResize)
        {
            _nextResize += _resizeEvery;
            var sizes = new[] { new Size(1280, 720), new Size(800, 600), new Size(1600, 900), new Size(1024, 768), new Size(640, 480) };
            ClientSize = sizes[_resizeStep++ % sizes.Length];
        }
        if (_moveEvery > 0 && t >= _nextMove)
        {
            _nextMove += _moveEvery;
            var offsets = new[] { new Point(100, 100), new Point(600, 200), new Point(-200, 50), new Point(1700, 300), new Point(300, 700) };
            Location = offsets[_moveStep++ % offsets.Length];
        }
        if (_minimizeAt > 0 && !_minimized && t >= _minimizeAt) { _minimized = true; WindowState = FormWindowState.Minimized; }
        if (_restoreAt > 0 && _minimized && !_restored && t >= _restoreAt) { _restored = true; WindowState = FormWindowState.Normal; }
        if (_closeAfter > 0 && t >= _closeAfter) { Close(); return; }

        _frame++;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.None;
        var cw = ClientSize.Width; var ch = ClientSize.Height;

        // Scrolling noise band so every frame differs everywhere (defeats damage-region optimizations).
        var band = (int)(_frame % 64);
        using (var pen = new Pen(Color.FromArgb(40, 40, 70)))
            for (var yy = band; yy < ch; yy += 64) g.DrawLine(pen, 0, yy, cw, yy);

        using (var brush = new SolidBrush(Color.FromArgb(74, 108, 247)))
            g.FillRectangle(brush, (float)_bx, (float)_by, 120, 120);

        // Corner markers for coordinate accuracy tests: 20 px squares at each corner.
        using (var red = new SolidBrush(Color.Red))
        {
            g.FillRectangle(red, 0, 0, 20, 20);
            g.FillRectangle(red, cw - 20, 0, 20, 20);
            g.FillRectangle(red, 0, ch - 20, 20, 20);
            g.FillRectangle(red, cw - 20, ch - 20, 20, 20);
        }

        var ms = _clock.ElapsedMilliseconds;
        g.DrawString($"{ms / 1000}.{ms % 1000:000}", _big, Brushes.White, 30, 30);
        g.DrawString($"frame {_frame}   {cw}x{ch}   client@{PointToScreen(Point.Empty)}", _small, Brushes.LightGray, 30, 100);
    }
}

using Chess;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinFormsButton = System.Windows.Forms.Button;
using WinFormsTrackBar = System.Windows.Forms.TrackBar;
using WinFormsTextBox = System.Windows.Forms.TextBox;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using Color = System.Drawing.Color;
using ImageSharpImage = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using Point = System.Drawing.Point;
using PointF = System.Drawing.PointF;
using Rectangle = System.Drawing.Rectangle;
using RectangleF = System.Drawing.RectangleF;
using Size = System.Drawing.Size;
using System.Runtime.CompilerServices;


namespace chessGUI
{
    public partial class Form1 : Form
    {
        private static long NowTicks() => Stopwatch.GetTimestamp();

        private static double TicksToMs(long ticks)
            => ticks * 1000.0 / Stopwatch.Frequency;

        private void LogTiming(string tag, long startTicks)
        {
            var ms = TicksToMs(NowTicks() - startTicks);
            FileLog.Write($"[PERF] {tag}: {ms:0.0} ms");
        }


        private readonly Panel _boardHost = new Panel();
        private readonly BoardControl _board = new BoardControl();
        private readonly WinFormsTrackBar _zoom = new WinFormsTrackBar(); 
        private readonly WinFormsTextBox _analysis = new WinFormsTextBox();

        private ChessPosition _pos;
        private readonly List<string> _fenHistory = new List<string>();
        private int _historyIndex = 0;

        private bool _suppressMovesSelectionChanged = false;

        private readonly List<string> _moveHistory = new List<string>(); // UCI moves, length = _fenHistory.Count - 1
        private readonly ListBox _moves = new ListBox();

        private UciEngine? _engine;
        private CancellationTokenSource? _analysisCts;

        private string _currentFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        private string? _engineExePath;

        private readonly object _fenProbeLock = new object();
        
        private WinFormsButton? _btnFirst;
        private WinFormsButton? _btnBack;
        private WinFormsButton? _btnForward;
        private WinFormsButton? _btnLast;

        private WinFormsButton? _btnStart;
        private WinFormsButton? _btnStop;
        private WinFormsButton? _btnAnalyse;
        private WinFormsButton? _btnNewGame;
        private WinFormsButton? _btnSetFen;
        private WinFormsButton? _btnImportPgn;
        private WinFormsButton? _btnExportPgn;

        private WinFormsButton? _btnFlip;

        private WinFormsButton? _btnAutoplay;
        private CancellationTokenSource? _autoplayCts;
        private bool _autoplayRunning = false;
        private string? _gifOutputPath;

        private WinFormsButton? _btnSelectEngine;

        private readonly Stopwatch _uiThrottleSw = Stopwatch.StartNew();
        private long _nextUiUpdateMs = 0;
        private int _lastDepthForArrow = -1;
        private int _lastDepthForLog = -1;

        private readonly SemaphoreSlim _analysisUpdateLock = new SemaphoreSlim(1, 1);
        private volatile string? _pendingAnalysisFen;
        private int _analysisUpdateScheduled = 0;

        private readonly object _bestmoveProbeLock = new object();
        private TaskCompletionSource<string>? _bestmoveProbeTcs;
        private volatile bool _suppressEngineUi = false;

        private readonly object _stopAckLock = new object();
        private TaskCompletionSource<bool>? _stopAckTcs;

        private bool _boardFlipped = false;

        // Click in Move list: Distinguish between white and black
        private bool _movesClickOverride = false;
        private int _movesClickHistoryIndex = 0;

        private bool _uiUnlocked = false; // Everything except the engine button is locked until the engine is truly ready.

        // Serializes ALL UCI sequences, prevents “best move” races.
        private readonly SemaphoreSlim _engineOpLock = new SemaphoreSlim(1, 1);

        private async void FlipBoard()
        {
            _boardFlipped = !_boardFlipped;
            _board.SetFlipped(_boardFlipped);   // oder Property
            _board.Invalidate();
        }


        public Form1()
        {
            InitializeComponent();

            FileLog.Init("chessGUI");
            FileLog.Write("Form1 ctor");

            // Globales Exception-Logging (UI + Background)
            Application.ThreadException += (_, e) =>
            {
                FileLog.Write("ThreadException: " + e.Exception);
                try { MessageBox.Show(e.Exception.ToString(), "ThreadException", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                FileLog.Write("UnhandledException: " + (e.ExceptionObject?.ToString() ?? "<null>"));
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                FileLog.Write("UnobservedTaskException: " + e.Exception);
                e.SetObserved();
            };


            Text = "UCI Chess GUI, v. 0.0.2, Dec 2025, Dr. Erhard Henkes ";
            Width = 1450;
            Height = 670;

            _engineExePath = AppConfig.Load().EngineExePath;

            _pos = ChessPosition.FromFen(_currentFen);
            _fenHistory.Clear();
            _fenHistory.Add(_pos.ToFen());
            _historyIndex = 0;
            _moveHistory.Clear();
            RefreshMovesUI();

            // Layout: links Brett+Zoom, rechts Analyse
            _boardHost.Dock = DockStyle.Left;
            _boardHost.Width = 640;
            _boardHost.Padding = new Padding(12);
            _boardHost.BackColor = Color.FromArgb(18, 18, 18);

            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Color.FromArgb(24, 24, 24) };

            _board.Dock = DockStyle.Fill;
            _board.Fen = _currentFen;

            _zoom.Dock = DockStyle.Bottom;
            _zoom.Minimum = 40;
            _zoom.Maximum = 120;
            _zoom.Value = 80;
            _zoom.TickFrequency = 10;
            _zoom.SmallChange = 2;
            _zoom.LargeChange = 10;
            _zoom.Height = 48;
            _zoom.ValueChanged += (_, __) => ApplyZoom();

            _analysis.Dock = DockStyle.Fill;
            _analysis.Multiline = true;
            _analysis.ReadOnly = true;
            _analysis.ScrollBars = ScrollBars.Vertical;
            _analysis.Font = new Font("Consolas", 10f);
            _analysis.BackColor = Color.FromArgb(16, 16, 16);
            _analysis.ForeColor = Color.Gainsboro;

            // 2-zeilige Topbar: Host + 2 FlowLayoutPanels
            var topBarHost = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 84,
                BackColor = Color.FromArgb(24, 24, 24),
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            topBarHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            topBarHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));
            topBarHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 42f));

            var topRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Height = 42,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(24, 24, 24),
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            var bottomRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                Height = 42,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(24, 24, 24),
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };

            topBarHost.Controls.Add(topRow, 0, 0);
            topBarHost.Controls.Add(bottomRow, 0, 1);

            _board.DragStarted += async (_, __) =>
            {
                _suppressEngineUi = true;          // UI updates from Engine
                await StopAnalysisImmediatelyAsync(); // Really stop analysis
            };

            _board.DragEnded += (_, __) =>
            {
                _suppressEngineUi = false;         // UI-Updates wieder an
                ResumeAnalysisIfRunning();         // Analyse wieder aufnehmen
            };

            // Buttons
            _btnFirst = new WinFormsButton { Text = "<<", Width = 56, Height = 30, Margin = new Padding(0, 6, 6, 6) };
            _btnBack = new WinFormsButton { Text = "<", Width = 56, Height = 30, Margin = new Padding(0, 6, 6, 6) };
            _btnForward = new WinFormsButton { Text = ">",  Width = 56, Height = 30, Margin = new Padding(0, 6, 6, 6) };
            _btnLast    = new WinFormsButton { Text = ">>", Width = 56, Height = 30, Margin = new Padding(0, 6, 10, 6) };

            _btnStart = new WinFormsButton { Text = "Engine Start", Width = 120, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            _btnStop = new WinFormsButton { Text = "Stop", Width = 80, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            _btnAnalyse = new WinFormsButton { Text = "Analysis", Width = 110, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            
            _btnNewGame = new WinFormsButton { Text = "NewGame", Width = 110, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            _btnSetFen = new WinFormsButton { Text = "FEN", Width = 90, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            
            _btnImportPgn = new WinFormsButton { Text = "PGN Import", Width = 130, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            _btnExportPgn = new WinFormsButton { Text = "PGN Export", Width = 130, Height = 30, Margin = new Padding(0, 6, 8, 6) };

            _btnFlip = new WinFormsButton { Text = "Flip", Width = 90, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            

            _btnFirst.Click += async (_, __) => await GoFirstAsync();
            _btnBack.Click += async (_, __) => await GoBackAsync();
            _btnForward.Click += async (_, __) => await GoForwardAsync();
            _btnLast.Click += async (_, __) => await GoLastAsync();

            _btnStart.Click += async (_, __) => await StartEngineAsync();
            _btnStop.Click += async (_, __) => await StopAnalysisOnlyAsync();
            _btnAnalyse.Click += async (_, __) => await StartAnalysisAsync();
           
            _btnNewGame.Click += async (_, __) => await NewGameAsync();
            _btnSetFen.Click += async (_, __) => await SetPositionFromFenDialogAsync();

            _btnImportPgn.Click += async (_, __) => await ImportPgnAsync();
            _btnExportPgn.Click += (_, __) => ExportPgn();

            _btnFlip.Click += (_, __) => FlipBoard();

            _btnAutoplay = new WinFormsButton { Text = "Autoplay", Width = 110, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            _btnAutoplay.Click += async (_, __) => await ToggleAutoplayAsync();

            _btnSelectEngine = new WinFormsButton { Text = "New Engine", Width = 140, Height = 30, Margin = new Padding(0, 6, 8, 6) };
            _btnSelectEngine.Click += async (_, __) => await SelectEngineAsync();

            static Control Sep() => new Panel { Width = 8, Height = 30, Margin = new Padding(6, 6, 6, 6), BackColor = Theme.Separator };

            // Zeile 1: << < > >> | Engine Start Stop Analyse New Game | Autoplay
            topRow.Controls.Add(_btnFirst);
            topRow.Controls.Add(_btnBack);
            topRow.Controls.Add(_btnForward);
            topRow.Controls.Add(_btnLast);

            topRow.Controls.Add(Sep()); 
            topRow.Controls.Add(_btnStart);
            topRow.Controls.Add(_btnStop);
            topRow.Controls.Add(_btnAnalyse);
            topRow.Controls.Add(_btnNewGame);
           

            topRow.Controls.Add(Sep());
            topRow.Controls.Add(_btnAutoplay);

            bottomRow.Controls.Add(_btnImportPgn);
            bottomRow.Controls.Add(_btnExportPgn);

            bottomRow.Controls.Add(Sep());
            bottomRow.Controls.Add(_btnSetFen);
            bottomRow.Controls.Add(_btnFlip);

            bottomRow.Controls.Add(Sep()); 
            bottomRow.Controls.Add(_btnSelectEngine);


            

            // Style
            StyleButton(_btnFirst, null);
            StyleButton(_btnBack, null);
            StyleButton(_btnForward, null);
            StyleButton(_btnLast, null);
            _btnFirst.Font = new Font("Segoe UI", 10f, FontStyle.Regular);
            _btnBack.Font = new Font("Segoe UI", 10f, FontStyle.Regular);
            _btnForward.Font = new Font("Segoe UI", 10f, FontStyle.Regular);
            _btnLast.Font = new Font("Segoe UI", 10f, FontStyle.Regular);
            _btnAnalyse.Font = new Font("Segoe UI", 10f, FontStyle.Regular);

            StyleButton(_btnStart, Theme.AccentEngine);
            StyleButton(_btnStop, Theme.Danger);
            StyleButton(_btnAnalyse, Theme.AccentAnalyse);
            StyleButton(_btnAutoplay, Theme.AccentAnalyse);
            StyleButton(_btnSelectEngine, null);

            StyleButton(_btnNewGame, null);
            StyleButton(_btnSetFen, null);
            StyleButton(_btnFlip, null);

            StyleButton(_btnImportPgn, null);
            StyleButton(_btnExportPgn, null);


            _boardHost.Controls.Add(_board);
            _boardHost.Controls.Add(_zoom);

            // Move list (below)
            _moves.Dock = DockStyle.Fill;
            _moves.Font = new Font("Consolas", 11f, FontStyle.Bold);
            _moves.BackColor = Color.FromArgb(16, 16, 16);
            _moves.ForeColor = Color.Gainsboro;
            _moves.BorderStyle = BorderStyle.FixedSingle;
            _moves.IntegralHeight = false;
            _moves.SelectionMode = SelectionMode.One;

            _moves.MouseDown += (_, e) =>
            {
                int row = _moves.IndexFromPoint(e.Location);
                if (row < 0) return;

                int whitePly = row * 2;         // Index in _moveHistory
                int whiteHistoryIndex = whitePly + 1; // Index in _fenHistory
                int blackPly = whitePly + 1;
                int blackHistoryIndex = whiteHistoryIndex + 1;

                // Split position (pixels): measure up to and including white space + space bar (Consolas => stable)
                string w = (whitePly >= 0 && whitePly < _moveHistory.Count) ? _moveHistory[whitePly] : "";
                string prefix = $"{(row + 1),2}. {w,-6} "; // corresponds to RefreshMovesUI format
                int splitX = TextRenderer.MeasureText(prefix, _moves.Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

                bool hasBlack = blackPly >= 0 && blackPly < _moveHistory.Count && !string.IsNullOrWhiteSpace(_moveHistory[blackPly]);

                int desired = (e.X <= splitX || !hasBlack) ? whiteHistoryIndex : blackHistoryIndex;

                // Bounds
                if (desired < 0) desired = 0;
                if (desired >= _fenHistory.Count) desired = _fenHistory.Count - 1;

                _movesClickOverride = true;
                _movesClickHistoryIndex = desired;
            };

            _moves.SelectedIndexChanged += async (_, __) =>
            {
                if (_suppressMovesSelectionChanged) return;
                if (_moves.SelectedIndex < 0) return;

                int targetHistoryIndex;

                if (_movesClickOverride)
                {
                    targetHistoryIndex = _movesClickHistoryIndex;
                    _movesClickOverride = false;
                }
                else
                {
                    // Fallback (e.g., keyboard): go to the end of the line (black, if available, otherwise white)
                    int row = _moves.SelectedIndex;
                    int whiteHistoryIndex = row * 2 + 1;
                    int blackHistoryIndex = whiteHistoryIndex + 1;
                    bool hasBlack = (row * 2 + 1) < _moveHistory.Count && !string.IsNullOrWhiteSpace(_moveHistory[row * 2 + 1]);

                    targetHistoryIndex = (hasBlack && blackHistoryIndex < _fenHistory.Count) ? blackHistoryIndex : whiteHistoryIndex;
                }

                if (targetHistoryIndex == _historyIndex) return;

                _historyIndex = targetHistoryIndex;
                _pos.LoadFen(_fenHistory[_historyIndex]);
                _board.SetPosition(_pos);

                _currentFen = _pos.ToFen();
                RequestAnalysisUpdate(_currentFen);

                RefreshMovesUI();
                UpdateNavButtons();
                await Task.CompletedTask;
            };

            // Split: analysis above, moves below
            var rightSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
                Panel1MinSize = 150,
                Panel2MinSize = 80,
                SplitterDistance = 420
            };

            rightSplit.Panel1.Controls.Add(_analysis);
            rightSplit.Panel2.Controls.Add(_moves);

            right.Controls.Add(rightSplit);
            right.Controls.Add(topBarHost);

            Controls.Add(right);
            Controls.Add(_boardHost);

            // Buttons easier to read
            var btnText = Color.Gold;      
            var btnBack = Color.FromArgb(40, 40, 40);

            foreach (Control c in topRow.Controls)
            {
                if (c is WinFormsButton b)
                {
                    b.ForeColor = btnText;
                    b.BackColor = btnBack;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
                    b.FlatAppearance.BorderSize = 1;
                }
            }
            foreach (Control c in bottomRow.Controls)
            {
                if (c is WinFormsButton b)
                {
                    b.ForeColor = btnText;
                    b.BackColor = btnBack;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
                    b.FlatAppearance.BorderSize = 1;
                }
            }

            Shown += async (_, __) =>
            {
                ApplyZoom();
                await Task.CompletedTask;
            };

            FormClosing += async (_, __) => await StopAnalysisAndEngineAsync();

            _board.UciMoveDropped += async (_, uciMove) =>
            {
                // Promotion? -> Dialog, then build 5-digit UCI
                string moveToSend = uciMove;

                if (TryGetPromotionContext(_pos.ToFen(), uciMove, out bool isWhitePawn))
                {
                    using var dlg = new PromotionDialog(isWhitePawn);
                    var dr = dlg.ShowDialog(this);
                    if (dr != DialogResult.OK || dlg.SelectedPromo == '\0')
                    {
                        _board.ClearDropPreview();
                        return;
                    }

                    // UCI: always lowercase q/r/b/n
                    moveToSend = uciMove + char.ToLowerInvariant(dlg.SelectedPromo);
                }

                var ok = await TryApplyUserMoveViaEngineAsync(moveToSend).ConfigureAwait(true);
                if (!ok)
                {
                    _board.ClearDropPreview();
                    AppendAnalysisLine($"[GUI] illegal/rejected: {moveToSend}");
                    BeepIllegalMove();
                }
            };

            _uiUnlocked = false;
            ApplyUiLockState();
            UpdateNavButtons();
        }

        private static bool TryGetPromotionContext(string fen, string uciMove4, out bool isWhitePawn)
        {
            isWhitePawn = true;

            if (string.IsNullOrWhiteSpace(fen)) return false;
            if (string.IsNullOrWhiteSpace(uciMove4) || uciMove4.Length != 4) return false;

            string from = uciMove4.Substring(0, 2);
            string to = uciMove4.Substring(2, 2);

            char piece = GetPieceAtSquareFromFen(fen, from);
            if (piece != 'P' && piece != 'p') return false;

            // target rank 8/1?
            char toRank = to[1];
            if (piece == 'P' && toRank != '8') return false;
            if (piece == 'p' && toRank != '1') return false;

            isWhitePawn = (piece == 'P');
            return true;
        }

        private static char GetPieceAtSquareFromFen(string fen, string sq)
        {
            try
            {
                if (sq.Length != 2) return '\0';
                int f = sq[0] - 'a';
                int rank = sq[1] - '0';
                if (f < 0 || f > 7 || rank < 1 || rank > 8) return '\0';

                int r = 8 - rank; // r=0 is rank 8

                var placement = fen.Split(' ')[0];
                var ranks = placement.Split('/');
                if (ranks.Length != 8) return '\0';

                int file = 0;
                foreach (var ch in ranks[r])
                {
                    if (char.IsDigit(ch))
                    {
                        file += (ch - '0');
                    }
                    else
                    {
                        if (file == f) return ch;
                        file++;
                    }
                }
                return '\0';
            }
            catch
            {
                return '\0';
            }
        }



        private void RefreshMovesUI()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(RefreshMovesUI)); return; }

            _moves.BeginUpdate();
            _suppressMovesSelectionChanged = true;
            try
            {
                _moves.Items.Clear();

                for (int i = 0; i < _moveHistory.Count; i += 2)
                {
                    int moveNo = (i / 2) + 1;
                    string w = _moveHistory[i];
                    string b = (i + 1 < _moveHistory.Count) ? _moveHistory[i + 1] : "";
                    _moves.Items.Add($"{moveNo,2}. {w,-6} {b}");
                }

                if (_historyIndex == 0)
                {
                    _moves.ClearSelected();
                }
                else
                {
                    int plyIndex = _historyIndex - 1;
                    int rowIndex = plyIndex / 2;
                    if (rowIndex >= 0 && rowIndex < _moves.Items.Count)
                        _moves.SelectedIndex = rowIndex;
                    else
                        _moves.ClearSelected();
                }
            }
            finally
            {
                _suppressMovesSelectionChanged = false;
                _moves.EndUpdate();
            }
        }
        private void UpdateNavButtons()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(UpdateNavButtons)); return; }

            if (!_uiUnlocked || _fenHistory.Count <= 1)
            {
                if (_btnFirst != null) _btnFirst.Enabled = false;
                if (_btnBack != null) _btnBack.Enabled = false;
                if (_btnForward != null) _btnForward.Enabled = false;
                if (_btnLast != null) _btnLast.Enabled = false;
                return;
            }

            bool canBack = _historyIndex > 0;
            bool canFwd = _historyIndex < _fenHistory.Count - 1;

            if (_btnFirst != null) _btnFirst.Enabled = canBack;
            if (_btnBack != null) _btnBack.Enabled = canBack;

            if (_btnForward != null) _btnForward.Enabled = canFwd;
            if (_btnLast != null) _btnLast.Enabled = canFwd;
        }


        private void ApplyUiLockState()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(ApplyUiLockState)); return; }

            // Always allow engine start
            if (_btnStart != null) _btnStart.Enabled = true;

            // Optional: Allow engine selection
            if (_btnSelectEngine != null) _btnSelectEngine.Enabled = true;

            bool on = _uiUnlocked;
            bool analysisRunning = _analysisCts != null;

            // Analysis only if UI is activated AND no analysis is currently running
            if (_btnAnalyse != null) _btnAnalyse.Enabled = on && !analysisRunning;

            // Stop only when analysis is running (opponent to analysis)
            if (_btnStop != null) _btnStop.Enabled = analysisRunning;

            if (_btnNewGame != null) _btnNewGame.Enabled = on;
            if (_btnSetFen != null) _btnSetFen.Enabled = on;
            if (_btnImportPgn != null) _btnImportPgn.Enabled = on;
            if (_btnExportPgn != null) _btnExportPgn.Enabled = on;
            if (_btnFlip != null) _btnFlip.Enabled = on;
            if (_btnAutoplay != null) _btnAutoplay.Enabled = on;

            if (_moves != null) _moves.Enabled = on;
            if (_board != null) _board.Enabled = on;

            if (!on)
            {
                if (_btnFirst != null) _btnFirst.Enabled = false;
                if (_btnBack != null) _btnBack.Enabled = false;
                if (_btnForward != null) _btnForward.Enabled = false;
                if (_btnLast != null) _btnLast.Enabled = false;
            }

            UpdateNavButtons();
        }

        private async Task GoBackAsync()
        {
            if (_historyIndex <= 0) return;

            _historyIndex--;
            _pos.LoadFen(_fenHistory[_historyIndex]);
            _board.SetPosition(_pos);
            RefreshMovesUI();
            UpdateNavButtons();
            RequestAnalysisUpdate(_pos.ToFen());
            await Task.CompletedTask;
        }

        private async Task GoForwardAsync()
        {
            if (_historyIndex >= _fenHistory.Count - 1) return;

            _historyIndex++;
            _pos.LoadFen(_fenHistory[_historyIndex]);
            _board.SetPosition(_pos);
            RefreshMovesUI();
            UpdateNavButtons();
            RequestAnalysisUpdate(_pos.ToFen());
            await Task.CompletedTask;

        }

        private async Task ApplyEngineOptionsAsync()
        {
            if (_engine == null) return;

            await _engineOpLock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Important: if a search is still running (analysis), stop it cleanly first,
                // otherwise “readyok” may be lost/arrive too late
                await StopSearchAndWaitBestmoveAsync(200, 500).ConfigureAwait(false);

                await _engine.SendAsync("setoption name Threads value 7").ConfigureAwait(false);
                await _engine.SendAsync("setoption name MultiPV value 3").ConfigureAwait(false);
                await _engine.SendAsync("setoption name Hash value 1600").ConfigureAwait(false);

                await _engine.SendAsync("isready").ConfigureAwait(false);
                await _engine.WaitForAsync("readyok", TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                AppendAnalysisLine("[GUI] Engine Optionen: Threads=7, MultiPV=3, Hash=1600");
            }
            finally
            {
                _engineOpLock.Release();
            }
        }


        private static string UciToSanOrUciFallback(string prevFen, string uciMove)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prevFen)) return uciMove;
                if (string.IsNullOrWhiteSpace(uciMove) || (uciMove.Length != 4 && uciMove.Length != 5)) return uciMove;

                string from = uciMove.Substring(0, 2);
                string to = uciMove.Substring(2, 2);

                // Gera.Chess can load from FEN and execute move objects. :contentReference[oaicite:2]{index=2}
                var b = ChessBoard.LoadFromFen(prevFen);

                // We'll run Promotion (uci.Length==5) as a fallback for now.
                // until we map it cleanly using a suitable move overload.
                if (uciMove.Length == 5)
                    return uciMove;

                b.Move(new Move(from, to));

                // Get SAN from PGN: extract last move token
                string pgn = b.ToPgn();

                // Remove result
                pgn = Regex.Replace(pgn, @"\b(1-0|0-1|1/2-1/2|\*)\b", " ").Trim();

                // Tokenize and take last "Move"-Token
                var tokens = pgn.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                string last = "";
                for (int i = tokens.Length - 1; i >= 0; i--)
                {
                    string t = tokens[i];

                    // Skip move numbers such as “12.” or “12...”
                    if (Regex.IsMatch(t, @"^\d+\.(\.\.)?$")) continue;

                    last = t;
                    break;
                }

                return string.IsNullOrWhiteSpace(last) ? uciMove : last;
            }
            catch
            {
                return uciMove;
            }
        }

        private async Task SelectEngineAsync()
        {
            StopAutoplay();

            using var ofd = new OpenFileDialog
            {
                Title = "UCI Engine auswählen (z.B. Stockfish.exe)",
                Filter = "Executables (*.exe)|*.exe|Alle Dateien (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (ofd.ShowDialog(this) != DialogResult.OK)
                return;

            _engineExePath = ofd.FileName;

            var cfg = AppConfig.Load();
            cfg.EngineExePath = _engineExePath;
            cfg.Save();

            AppendAnalysisLine($"[GUI] Engine set: {_engineExePath}");

            // Optional: if engine is currently running -> restart cleanly
            if (_engine != null)
            {
                await StopAnalysisAndEngineAsync().ConfigureAwait(false);
                await StartEngineAsync().ConfigureAwait(false);
            }
        }

        private async Task StopAnalysisImmediatelyAsync()
        {
            if (_engine == null) return;
            if (_analysisCts == null) return;

            await StopSearchAndWaitBestmoveAsync(0).ConfigureAwait(false);
        }


        private async Task StopSearchAndWaitBestmoveAsync(int bestmoveTimeoutMs = 100, int readyTimeoutMs = 250)
        {
            if (_engine == null) return;

            var tAll = NowTicks();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stopAckLock)
            {
                _stopAckTcs = tcs;
            }

            try
            {
                {
                    var t = NowTicks();
                    await _engine.SendAsync("stop").ConfigureAwait(false);
                    LogTiming("StopSearch: SendAsync(stop)", t);
                }

                // Optional: wait briefly for bestmove-ack. If <= 0, skip.
                if (bestmoveTimeoutMs > 0)
                {
                    try
                    {
                        var t = NowTicks();
                        using var cts = new CancellationTokenSource(bestmoveTimeoutMs);
                        await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                        LogTiming($"StopSearch: Wait bestmove ack ({bestmoveTimeoutMs}ms)", t);
                    }
                    catch
                    {
                        FileLog.Write($"[PERF] StopSearch: bestmove ack TIMEOUT after {bestmoveTimeoutMs}ms");
                    }
                }
                else
                {
                    FileLog.Write("[PERF] StopSearch: bestmove ack WAIT SKIPPED");
                }



                // Then bring the engine safely to idle (briefly).
                try
                {
                    var t = NowTicks();
                    await _engine.SendAsync("isready").ConfigureAwait(false);
                    await _engine.WaitForAsync("readyok", TimeSpan.FromMilliseconds(readyTimeoutMs)).ConfigureAwait(false);
                    LogTiming($"StopSearch: isready+readyok ({readyTimeoutMs}ms)", t);
                }
                catch
                {
                    FileLog.Write($"[PERF] StopSearch: readyok TIMEOUT after {readyTimeoutMs}ms");
                }
            }
            finally
            {
                lock (_stopAckLock)
                {
                    if (ReferenceEquals(_stopAckTcs, tcs))
                        _stopAckTcs = null;
                }

                LogTiming("StopSearchAndWaitBestmoveAsync TOTAL", tAll);
            }
        }



        private void ResumeAnalysisIfRunning()
        {
            if (_analysisCts == null) return;
            RequestAnalysisUpdate(_currentFen); // blockiert nicht
        }

        private async Task ImportPgnAsync()
        {
            StopAutoplay();

            bool wasAnalysisRunning = (_analysisCts != null);

            // Stop analysis cleanly (don't just send “stop”)
            if (wasAnalysisRunning)
                await StopAnalysisOnlyAsync().ConfigureAwait(false);

            using var ofd = new OpenFileDialog
            {
                Title = "Import PGN Datei importieren (main line)",
                Filter = "PGN (*.pgn)|*.pgn|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (ofd.ShowDialog(this) != DialogResult.OK)
                return;

            string pgn = File.ReadAllText(ofd.FileName, Encoding.UTF8);

            // Hauptlinie extrahieren (Kommentare/Varianten raus)
            List<string> sanMoves = PgnMainlineExtractor.ExtractMainlineSan(pgn);

            if (sanMoves.Count == 0)
            {
                MessageBox.Show("No moves found in the PGN (after removing variations/comments).",
                    "PGN Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // stop analysis
            if (_engine != null && _analysisCts != null)
                await _engine.SendAsync("stop").ConfigureAwait(false);

            // Everything back to start (history empty)
            await NewGameAsync().ConfigureAwait(false);

            // Gera.Chess: Run SAN + collect FEN after every half move :contentReference[oaicite:2]{index=2}
            var board = new ChessBoard(); // Startpos

            // We are rebuilding History from scratch (starting position + n half moves).
            _fenHistory.Clear();
            _moveHistory.Clear();
            _historyIndex = 0;

            // Start FEN from Gera.Chess -> import into our model
            string startFen = board.ToFen(); // Available according to Gera.Chess documentation: contentReference[oaicite:3]{index=3}
            _pos.LoadFen(startFen);
            _fenHistory.Add(_pos.ToFen());

            int importedPlies = 0;

            foreach (var san in sanMoves)
            {
                try
                {
                    board.Move(san); // Execute SAN :contentReference[oaicite:4]{index=4}
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"PGN Import abgebrochen bei Zug:\n{san}\n\n{ex.Message}",
                        "PGN Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                }

                string fen = board.ToFen();
                _pos.LoadFen(fen);

                _moveHistory.Add(san);      // Display: SAN (matches PGN)
                _fenHistory.Add(_pos.ToFen());
                _historyIndex = _fenHistory.Count - 1;
                importedPlies++;
            }

            _board.SetPosition(_pos);
            RefreshMovesUI();
            UpdateNavButtons();

            AppendAnalysisLine($"[GUI] PGN imported: {importedPlies} plies (main line).");

            await StartEngineAsync().ConfigureAwait(false);          // If not already started
            await ApplyEngineOptionsAsync().ConfigureAwait(false);   // Set options securely

            // Only restart the analysis if it was running previously
            if (wasAnalysisRunning)
                await StartAnalysisAsync().ConfigureAwait(false);

        }

        private void ExportPgn()
        {
            // Exporting the start position alone makes little sense, but we allow it.
            using var sfd = new SaveFileDialog
            {
                Title = "save PGN",
                Filter = "PGN (*.pgn)|*.pgn|all files (*.*)|*.*",
                FileName = "game.pgn",
                OverwritePrompt = true
            };

            if (sfd.ShowDialog(this) != DialogResult.OK)
                return;

            string pgn = BuildPgnFromSanHistory();

            File.WriteAllText(sfd.FileName, pgn, Encoding.UTF8);
            AppendAnalysisLine($"[GUI] PGN exported: {sfd.FileName}");
        }

        private string BuildPgnFromSanHistory()
        {
            // Minimal-Header
            var sb = new StringBuilder();
            sb.AppendLine("[Event \"chessGUI\"]");
            sb.AppendLine("[Site \"Local\"]");
            sb.AppendLine($"[Date \"{DateTime.Now:yyyy.MM.dd}\"]");
            sb.AppendLine("[Round \"-\"]");
            sb.AppendLine("[White \"-\"]");
            sb.AppendLine("[Black \"-\"]");
            sb.AppendLine("[Result \"*\"]");
            sb.AppendLine();

            // Moves: _moveHistory contains SAN (created by you or during import)
            for (int i = 0; i < _moveHistory.Count; i += 2)
            {
                int moveNo = (i / 2) + 1;
                sb.Append(moveNo.ToString(CultureInfo.InvariantCulture));
                sb.Append(". ");
                sb.Append(_moveHistory[i]);

                if (i + 1 < _moveHistory.Count)
                {
                    sb.Append(' ');
                    sb.Append(_moveHistory[i + 1]);
                }

                sb.Append(' ');
            }

            sb.Append("*");
            sb.AppendLine();
            return sb.ToString();
        }

        private async Task ToggleAutoplayAsync()
        {
            const int FrameMs = 1000;

            if (_autoplayRunning)
            {
                FileLog.Write("Autoplay STOP requested (already running).");
                StopAutoplay();
                return;
            }

            if (_fenHistory.Count <= 1)
            {
                FileLog.Write("Autoplay ABORT: no game loaded (_fenHistory.Count <= 1).");
                MessageBox.Show("Keine Partie geladen. Bitte erst PGN importieren.", "Autoplay",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!TrySelectGifOutputPath())
            {
                FileLog.Write("Autoplay ABORT: no GIF output selected.");
                AppendAnalysisLine("[GUI] Autoplay canceled (no GIF target selected).");
                return;
            }

            _autoplayRunning = true;
            _autoplayCts = new CancellationTokenSource();
            await GoToStartAsync(); // Always start with move 1

            FileLog.Write($"Autoplay START: historyIndex={_historyIndex} / last={(_fenHistory.Count - 1)}");

            UI(() => { if (_btnAutoplay != null) _btnAutoplay.Text = "Stop Play"; });
            UI(() => AppendAnalysisLine("[GUI] Autoplay starting..."));

            try
            {
                await StartAnalysisAsync(); // UI-Thread

                var frames = new List<byte[]>();

                // Frame 0: current position (start / current index)
                frames.Add(CaptureBoardPngBytes());

                while (_historyIndex < _fenHistory.Count - 1)
                {
                    _autoplayCts.Token.ThrowIfCancellationRequested();

                    await GoForwardAsync(); // UI-safe

                    // After stepping forward, capture the frame.
                    frames.Add(CaptureBoardPngBytes());

                    await Task.Delay(FrameMs, _autoplayCts.Token);
                }

                UI(() => AppendAnalysisLine("[GUI] Autoplay complete (end position)."));
                FileLog.Write($"GIF: encoding frames={frames.Count} -> {_gifOutputPath}");

                if (!string.IsNullOrWhiteSpace(_gifOutputPath))
                {
                    // Encoding deliberately not on UI thread
                    string path = _gifOutputPath;
                    await Task.Run(async () => await SaveGifAsync(frames, path, FrameMs).ConfigureAwait(false)).ConfigureAwait(false);
                    UI(() => AppendAnalysisLine($"[GUI] GIF saved: {_gifOutputPath}"));
                    FileLog.Write($"GIF: saved {path}");
                }

            }
            catch (OperationCanceledException)
            {
                AppendAnalysisLine("[GUI] Autoplay stopped.");
                FileLog.Write("Autoplay CANCELED.");
            }
            catch (Exception ex)
            {
                AppendAnalysisLine("[GUI] Autoplay ERROR: " + ex.GetType().Name + " - " + ex.Message);
                FileLog.Write("Autoplay ERROR: " + ex);
                MessageBox.Show(ex.ToString(), "Autoplay Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _autoplayRunning = false;
                _autoplayCts?.Dispose();
                _autoplayCts = null;

                UI(() => { if (_btnAutoplay != null) _btnAutoplay.Text = "Autoplay"; });
                FileLog.Write("Autoplay FINALLY (state reset).");
            }
        }



        private void StopAutoplay()
        {
            _autoplayCts?.Cancel();
        }

        private void UI(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        private Task GoToStartAsync()
        {
            var tcs = new TaskCompletionSource();

            UI(() =>
            {
                try
                {
                    _historyIndex = 0;
                    _pos.LoadFen(_fenHistory[0]);
                    _board.SetPosition(_pos);
                    UpdateNavButtons();
                    RequestAnalysisUpdate(_pos.ToFen()); // blockiert nicht
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            return tcs.Task;
        }


        private bool TrySelectGifOutputPath()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "save GIF",
                Filter = "GIF (*.gif)|*.gif",
                FileName = "chess_autoplay.gif",
                AddExtension = true,
                OverwritePrompt = true
            };

            if (sfd.ShowDialog(this) != DialogResult.OK)
                return false;

            _gifOutputPath = sfd.FileName;
            return true;
        }

        private void RequestAnalysisUpdate(string fen)
        {
            _currentFen = fen;
            _pendingAnalysisFen = fen;

            // Only when analysis is running and engine is present
            if (_engine == null || _analysisCts == null)
                return;

            // Coalesce: start only 1 worker at a time
            if (Interlocked.Exchange(ref _analysisUpdateScheduled, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _analysisUpdateLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        while (true)
                        {
                            string? f = _pendingAnalysisFen;
                            _pendingAnalysisFen = null;

                            if (f == null) break;
                            if (_engine == null || _analysisCts == null) break;

                            // UCI sequence MUST be exclusive, otherwise “bestmove” will conflict with Probe/Stop.
                            await _engineOpLock.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                if (_engine == null || _analysisCts == null) break;

                                // Analysis transition: stop -> (best move ack) -> position -> go infinite
                                await StopSearchAndWaitBestmoveAsync(0).ConfigureAwait(false);

                                await _engine.SendAsync($"position fen {f}").ConfigureAwait(false);
                                await _engine.SendAsync("go infinite").ConfigureAwait(false);
                            }
                            finally
                            {
                                _engineOpLock.Release();
                            }

                            // In case a new Fen request came in again in the meantime: loop
                            if (_pendingAnalysisFen == null) break;
                        }
                    }
                    finally
                    {
                        _analysisUpdateLock.Release();
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write("RequestAnalysisUpdate worker ERROR: " + ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _analysisUpdateScheduled, 0);

                    // If someone has set a new Fen during reset, kick it again immediately.
                    if (_pendingAnalysisFen != null && _engine != null && _analysisCts != null)
                        RequestAnalysisUpdate(_pendingAnalysisFen);
                }
            });
        }

        private async Task NewGameAsync()
        {
            StopAutoplay();

            // stop analysis (if running)
            if (_engine != null && _analysisCts != null)
                await _engine.SendAsync("stop").ConfigureAwait(false);

            _currentFen = StartFen;

            _pos = ChessPosition.FromFen(StartFen);

            _fenHistory.Clear();
            _fenHistory.Add(_pos.ToFen());
            _historyIndex = 0;
            _moveHistory.Clear();
            RefreshMovesUI();

            _board.SetPosition(_pos);

            _analysis.Clear();
            AppendAnalysisLine("[GUI] New Game.");

            // send position startpos
            if (_engine != null)
            {
                await _engine.SendAsync("setoption name MultiPV value 3").ConfigureAwait(false);
                await _engine.SendAsync("setoption name Threads value 7").ConfigureAwait(false);
                await _engine.SendAsync("setoption name Hash value 1600").ConfigureAwait(false);
                await _engine.SendAsync("isready").ConfigureAwait(false);
                await _engine.WaitForAsync("readyok", TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                await _engine.SendAsync("position startpos").ConfigureAwait(false);
            }

            UpdateNavButtons();

            await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
            _board.SetBestMoveArrow(null, null);
        }

        private async Task SetPositionFromFenDialogAsync()
        {
            StopAutoplay();

            using var dlg = new FenInputDialog
            {
                StartPosition = FormStartPosition.CenterParent,
                FenText = _currentFen
            };

            if (dlg.ShowDialog(this) != DialogResult.OK)
                return;

            string fen = (dlg.FenText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(fen))
                return;

            // Make it more tolerant: Reduce multiple spaces
            fen = Regex.Replace(fen, @"\s+", " ");

            // Many users only have 4 fields (without halfmove/fullmove) – UCI usually expects 6 -> fill in
            fen = EnsureFen6Fields(fen);

            // Validate: your ChessPosition can load FEN (we use try/catch as a validator)
            try
            {
                var tmp = ChessPosition.FromFen(fen);  
                fen = tmp.ToFen();                     
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Ungültige FEN:\n\n" + ex.Message, "FEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            await LoadFenAsNewGameAsync(fen).ConfigureAwait(false);
        }
        private static string EnsureFen6Fields(string fen)
        {
            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 6) return fen;

            // Minimum: 4 fields are standard (placement, side, castling, ep)
            if (parts.Length == 4)
                return fen + " 0 1";

            // 5 fields -> full move missing
            if (parts.Length == 5)
                return fen + " 1";

            return fen; // lassen, Validation fängt dann ab
        }

        private async Task LoadFenAsNewGameAsync(string fen)
        {
            // Stop analysis (if running)
            if (_engine != null && _analysisCts != null)
                await _engine.SendAsync("stop").ConfigureAwait(false);

            _currentFen = fen;

            _pos = ChessPosition.FromFen(fen);

            // History komplett neu: Start = diese FEN
            _fenHistory.Clear();
            _fenHistory.Add(_pos.ToFen());
            _historyIndex = 0;

            _moveHistory.Clear();
            RefreshMovesUI();

            _board.SetPosition(_pos);
            UpdateNavButtons();

            _analysis.Clear();
            AppendAnalysisLine("[GUI] Set position from FEN.");
            AppendAnalysisLine("[GUI] FEN: " + _pos.ToFen());

            //  Set engine to this position (instead of position startpos): contentReference[oaicite:5]{index=5}
            if (_engine != null)
            {
                await ApplyEngineOptionsAsync().ConfigureAwait(false); // sets threads/multiPV/hash + readyok :contentReference[oaicite:6]{index=6}
                await _engine.SendAsync($"position fen {_pos.ToFen()}").ConfigureAwait(false);
            }

            _board.SetBestMoveArrow(null, null);

            // If analysis is running: restart immediately (coalesce mechanism will handle this cleanly) :contentReference[oaicite:7]{index=7}
            await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
        }
                

        private async Task GoFirstAsync()
        {
            if (_historyIndex <= 0) return;

            _historyIndex = 0;
            _pos.LoadFen(_fenHistory[_historyIndex]);
            _board.SetPosition(_pos);

            _currentFen = _pos.ToFen();
            RequestAnalysisUpdate(_currentFen);

            RefreshMovesUI();
            UpdateNavButtons();
            await Task.CompletedTask;
        }

        private async Task GoLastAsync()
        {
            if (_historyIndex >= _fenHistory.Count - 1) return;

            _historyIndex = _fenHistory.Count - 1;
            _pos.LoadFen(_fenHistory[_historyIndex]);
            _board.SetPosition(_pos);

            _currentFen = _pos.ToFen();
            RequestAnalysisUpdate(_currentFen);

            RefreshMovesUI();
            UpdateNavButtons();
            await Task.CompletedTask;
        }

        private async Task<bool> TryApplyUserMoveViaEngineAsync(string uciMove)
        {
            var tAll = NowTicks();
            FileLog.Write($"[PERF] MoveStart {uciMove}");

            if (_engine == null)
            {
                var t = NowTicks();
                await StartEngineAsync();
                LogTiming("StartEngineAsync", t);

                if (_engine == null)
                {
                    LogTiming("TryApplyUserMoveViaEngineAsync TOTAL (engine null)", tAll);
                    return false;
                }
            }

            // If analysis is running: stop immediately
            if (_analysisCts != null)
            {
                var t = NowTicks();
                await _engine.SendAsync("stop").ConfigureAwait(false);
                LogTiming("SendAsync(stop) (pre-legal)", t);
            }

            string prevFenForSan = _pos.ToFen();

            // 1) Legality via engine
            {
                var t = NowTicks();
                bool legal = await IsUciMoveLegalByEngineAsync(prevFenForSan, uciMove).ConfigureAwait(false);
                LogTiming("IsUciMoveLegalByEngineAsync", t);

                if (!legal)
                {
                    AppendAnalysisLine($"[GUI] illegal/rejected: {uciMove}");
                    BeepIllegalMove();

                    var t2 = NowTicks();
                    await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
                    LogTiming("RestartAnalysisIfRunningAsync (after illegal)", t2);

                    LogTiming("TryApplyUserMoveViaEngineAsync TOTAL (illegal)", tAll);
                    return false;
                }
            }

            // 2) Next position via Gera.Chess
            {
                var t = NowTicks();
                if (!TryApplyUciWithGeraChess(prevFenForSan, uciMove, out string nextFen))
                {
                    AppendAnalysisLine($"[GUI] illegal/rejected (gera-fail): {uciMove}");

                    var t2 = NowTicks();
                    await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
                    LogTiming("RestartAnalysisIfRunningAsync (after gera-fail)", t2);

                    LogTiming("TryApplyUserMoveViaEngineAsync TOTAL (gera-fail)", tAll);
                    return false;
                }
                LogTiming("TryApplyUciWithGeraChess", t);

                _pos.LoadFen(nextFen);
                _currentFen = _pos.ToFen();
            }

            // History update
            {
                var t = NowTicks();

                if (_historyIndex < _fenHistory.Count - 1)
                {
                    _fenHistory.RemoveRange(_historyIndex + 1, _fenHistory.Count - (_historyIndex + 1));

                    int keepPlies = _historyIndex;
                    if (_moveHistory.Count > keepPlies)
                        _moveHistory.RemoveRange(keepPlies, _moveHistory.Count - keepPlies);
                }

                _fenHistory.Add(_pos.ToFen());
                _historyIndex = _fenHistory.Count - 1;

                string san = UciToSanOrUciFallback(prevFenForSan, uciMove);
                _moveHistory.Add(san);

                LogTiming("History+SAN", t);
            }

            // UI update
            {
                var t = NowTicks();
                RefreshMovesUI();
                LogTiming("RefreshMovesUI", t);
            }

            {
                var t = NowTicks();
                _board.SetPosition(_pos);
                UpdateNavButtons();
                LogTiming("Board.SetPosition + UpdateNavButtons", t);
            }

            // start analysis
            {
                var t = NowTicks();
                RequestAnalysisUpdate(_pos.ToFen());
                LogTiming("RequestAnalysisUpdate", t);
            }

            AppendAnalysisLine($"[GUI] applied: {uciMove}  FEN: {_pos.ToFen()}");
            LogTiming("TryApplyUserMoveViaEngineAsync TOTAL (ok)", tAll);
            return true;
        }


        private static bool TryApplyUciWithGeraChess(string prevFen, string uciMove, out string nextFen)
        {
            nextFen = prevFen;

            try
            {
                if (string.IsNullOrWhiteSpace(prevFen)) return false;
                if (string.IsNullOrWhiteSpace(uciMove) || (uciMove.Length != 4 && uciMove.Length != 5)) return false;

                string from = uciMove.Substring(0, 2);
                string to = uciMove.Substring(2, 2);

                // 1) First try Gera.Chess (preferably for EP/Castling/50-move etc.)
                try
                {
                    var board = ChessBoard.LoadFromFen(prevFen);

                    Move move;
                    if (uciMove.Length == 5)
                    {
                        char promoRaw = uciMove[4]; // q/r/b/n aus UCI
                        char promoLower = char.ToLowerInvariant(promoRaw);
                        char promoUpper = char.ToUpperInvariant(promoRaw);

                        // Some Gera.Chess versions use (from,to,char), but may expect Q/R/B/N instead of q/r/b/n.
                        var ctor = typeof(Move).GetConstructor(new[] { typeof(string), typeof(string), typeof(char) });
                        if (ctor == null)
                            throw new InvalidOperationException("Move(from,to,char) ctor missing");

                        // trial 1: lower
                        try
                        {
                            move = (Move)ctor.Invoke(new object[] { from, to, promoLower });
                            board.Move(move);
                            nextFen = board.ToFen();
                            return true;
                        }
                        catch
                        {
                            // trial 2: upper
                            move = (Move)ctor.Invoke(new object[] { from, to, promoUpper });
                            board.Move(move);
                            nextFen = board.ToFen();
                            return true;
                        }
                    }
                    else
                    {
                        move = new Move(from, to);
                        board.Move(move);
                        nextFen = board.ToFen();
                        return true;
                    }
                }
                catch
                {
                    // 2) Fallback: internal FEN mover (absolutely sufficient for promotion)
                    var p = ChessPosition.FromFen(prevFen);
                    if (!p.TryApplyUciMove(uciMove))
                        return false;

                    nextFen = p.ToFen();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }


        private void ApplyZoom()
        {
            var size = (int)Math.Round(_zoom.Value / 100.0 * 640);
            size = Math.Max(300, Math.Min(860, size));

            _boardHost.Width = size + 24; // Padding
            _board.Size = new Size(size, size);
            _board.Invalidate();
        }

        private async Task StartEngineAsync()
        {
            if (_engine != null) return;

            // 1) Load/check path, otherwise select
            if (string.IsNullOrWhiteSpace(_engineExePath) || !File.Exists(_engineExePath))
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "UCI Engine auswählen (z.B. Stockfish.exe)",
                    Filter = "Executables (*.exe)|*.exe|Alle Dateien (*.*)|*.*",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (ofd.ShowDialog(this) != DialogResult.OK)
                    return;

                _engineExePath = ofd.FileName;

                var cfg = AppConfig.Load();
                cfg.EngineExePath = _engineExePath;
                cfg.Save();
            }

            // 2) Start engine (WorkingDirectory = Engine folder => Text files there)
            _engine = new UciEngine(_engineExePath, HandleEngineLine);

            await _engine.StartAsync().ConfigureAwait(false);

            await _engine.SendAsync("uci").ConfigureAwait(false);
            await _engine.WaitForAsync("uciok", TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            await ApplyEngineOptionsAsync().ConfigureAwait(false);

            AppendAnalysisLine("[GUI] Engine ready.");
            UI(() => _board.SetBestMoveArrow(null, null));
            _uiUnlocked = true;
            ApplyUiLockState();
            UpdateNavButtons();

            SetButtonActive(_btnStart, true, Theme.AccentEngine);
            SetButtonActive(_btnStop, false, Theme.Danger);
        }

        private async Task StartAnalysisAsync()
        {
            if (_engine == null)
            {
                await StartEngineAsync();
                if (_engine == null) return;
            }

            _analysisCts?.Cancel();
            _analysisCts = new CancellationTokenSource();
            ApplyUiLockState();   // Stop is now enabled

            SetButtonActive(_btnAnalyse, true, Theme.AccentAnalyse);

            _analysis.Clear();
            AppendAnalysisLine("[GUI] Analysis starting...");

            RequestAnalysisUpdate(_currentFen);
            await Task.CompletedTask;
        }

        private async Task RestartAnalysisIfRunningAsync()
        {
            if (_engine == null) return;
            if (_analysisCts == null) return;

            RequestAnalysisUpdate(_currentFen);
            await Task.CompletedTask;
        }

        private async Task StopAnalysisOnlyAsync()
        {
            if (_engine == null) return;
            if (_analysisCts == null) return;

            await _engineOpLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopSearchAndWaitBestmoveAsync(0).ConfigureAwait(false);
            }
            finally
            {
                _engineOpLock.Release();
            }

            _analysisCts?.Cancel();
            _analysisCts = null;

            // Remove Bestmove arrow (fix cosmetic flaw)
            _board.SetBestMoveArrow(null, null);

            ApplyUiLockState();
            SetButtonActive(_btnAnalyse, false, Theme.AccentAnalyse);
            SetButtonActive(_btnStop, false, Theme.Danger);
        }

        private async Task StopAnalysisAndEngineAsync()
        {
            try
            {
                if (_engine != null)
                {
                    await _engine.SendAsync("stop").ConfigureAwait(false);
                    await _engine.SendAsync("quit").ConfigureAwait(false);
                }
            }
            catch
            {
                // deliberately minimal
            }
            finally
            {
                _analysisCts?.Cancel();
                _analysisCts = null;
                ApplyUiLockState();   // Stop will now be disabled

                _engine?.Dispose();
                _engine = null;

                _board.SetBestMoveArrow(null, null);

                SetButtonActive(_btnAnalyse, false, Theme.AccentAnalyse);
                SetButtonActive(_btnStart, false, Theme.AccentEngine);
                SetButtonActive(_btnStop, false, Theme.Danger);
            }
        }

        private static void BeepIllegalMove()
        {
            try
            {
                System.Media.SystemSounds.Beep.Play(); // alternatively: SystemSounds.Hand / Exclamation / Asterisk
            }
            catch
            {
                // deliberately minimal
            }
        }


        private void HandleEngineLine(string line)
        {
            if (_suppressEngineUi)
            {
                // Important: Allow bestmove trial anyway (legality check)
                if (!line.StartsWith("bestmove ", StringComparison.Ordinal))
                    return;
            }

            // 1) bestmove sample for searchmoves (legality check) – thread-safe
            if (line.StartsWith("bestmove ", StringComparison.Ordinal))
            {
                // A) Ack für StopAnalysisImmediatelyAsync (stop -> bestmove)
                TaskCompletionSource<bool>? stopAck = null;
                lock (_stopAckLock)
                {
                    stopAck = _stopAckTcs;
                    _stopAckTcs = null;
                }
                stopAck?.TrySetResult(true);

                // B) bestmove-Probe (Legalitätscheck)
                TaskCompletionSource<string>? tcs = null;
                lock (_bestmoveProbeLock)
                {
                    tcs = _bestmoveProbeTcs;
                    _bestmoveProbeTcs = null;
                }
                tcs?.TrySetResult(line);
            }

            // 2) Info lines: parsing in the reader thread, UI only throttled
            if (line.StartsWith("info ", StringComparison.Ordinal))
            {
                var parsed = UciInfo.TryParse(line);
                if (parsed == null) return;

                // only PV1
                if (parsed.MultiPv.HasValue && parsed.MultiPv.Value != 1) return;

                long now = _uiThrottleSw.ElapsedMilliseconds;
                if (now < _nextUiUpdateMs) return;
                _nextUiUpdateMs = now + 200;

                // Arrow only for new depth
                if (parsed.Depth.HasValue && parsed.Depth.Value != _lastDepthForArrow)
                {
                    _lastDepthForArrow = parsed.Depth.Value;

                    if (!string.IsNullOrWhiteSpace(parsed.Pv))
                    {
                        var first = parsed.Pv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (first.Length > 0 && first[0].Length >= 4)
                        {
                            // Arrow ONLY during analysis (legality check should not set an arrow)
                            if (_analysisCts != null && !_analysisCts.IsCancellationRequested)
                            {
                                string mv = first[0];
                                string from = mv.Substring(0, 2);
                                string to = mv.Substring(2, 2);

                                UI(() => _board.SetBestMoveArrow(from, to));
                            }
                        }
                    }
                }

                // Log only for new depth
                if (parsed.Depth.HasValue && parsed.Depth.Value != _lastDepthForLog)
                {
                    _lastDepthForLog = parsed.Depth.Value;
                    var txt = parsed.ToDisplayString();
                    UI(() => AppendAnalysisLine(txt));
                }

                return;
            }

            // 3) bestmove show normal (UI)
            if (line.StartsWith("bestmove ", StringComparison.Ordinal))
            {
                var s = line.Trim();
                UI(() => AppendAnalysisLine(s));
            }
        }


        // ====== NEW: Legality check via UCI standard “searchmoves” ======
        private async Task<bool> IsUciMoveLegalByEngineAsync(string prevFen, string uciMove)
        {
            if (_engine == null) return false;

            var tAll = NowTicks();

            await _engineOpLock.WaitAsync().ConfigureAwait(false);
            try
            {
                {
                    var t = NowTicks();
                    await StopSearchAndWaitBestmoveAsync(0).ConfigureAwait(false);
                    LogTiming("Legal: StopSearchAndWaitBestmoveAsync(0)", t);
                }

                var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_bestmoveProbeLock) { _bestmoveProbeTcs = tcs; }

                {
                    var t = NowTicks();
                    await _engine.SendAsync($"position fen {prevFen}").ConfigureAwait(false);
                    LogTiming("Legal: SendAsync(position fen)", t);
                }

                {
                    var t = NowTicks();
                    await _engine.SendAsync($"go depth 1 searchmoves {uciMove}").ConfigureAwait(false);
                    LogTiming("Legal: SendAsync(go depth 1 searchmoves)", t);
                }

                string line;
                {
                    var t = NowTicks();
                    using var cts = new CancellationTokenSource(1500);
                    line = await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                    LogTiming("Legal: Wait bestmove line", t);
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    LogTiming("IsUciMoveLegalByEngineAsync TOTAL (bad line)", tAll);
                    return false;
                }

                var best = parts[1].Trim();
                var ok = string.Equals(best, uciMove, StringComparison.OrdinalIgnoreCase);
                LogTiming("IsUciMoveLegalByEngineAsync TOTAL", tAll);
                return ok;
            }
            catch
            {
                lock (_bestmoveProbeLock) { _bestmoveProbeTcs = null; }
                LogTiming("IsUciMoveLegalByEngineAsync TOTAL (exception)", tAll);
                return false;
            }
            finally
            {
                _engineOpLock.Release();
            }
        }

        private void AppendAnalysisLine(string s)
        {
            FileLog.Write("UI: " + s);

            if (IsDisposed) return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => AppendAnalysisLine(s)));
                return;
            }

            if (_analysis.TextLength > 200_000)
            {
                _analysis.Clear();
                _analysis.AppendText("[GUI] Log truncated.\r\n");
            }

            _analysis.AppendText(s + Environment.NewLine);
        }

        private void StyleButton(WinFormsButton b, Color? accent = null)
        {
            var normal = Theme.ButtonBg;
            var hover = Theme.ButtonHover;
            var down = Theme.ButtonDown;

            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = Theme.Border;
            b.UseVisualStyleBackColor = false;
            b.BackColor = normal;
            b.ForeColor = Theme.TextPrimary;
            b.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            b.AutoSize = false;
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Padding = new Padding(4, 0, 4, 0);

            // Hover/Pressed Feedback
            b.MouseEnter += (_, __) => { if (b.Enabled) b.BackColor = hover; };
            b.MouseLeave += (_, __) => { if (b.Enabled) b.BackColor = normal; };
            b.MouseDown += (_, __) => { if (b.Enabled) b.BackColor = down; };
            b.MouseUp += (_, __) => { if (b.Enabled) b.BackColor = b.Bounds.Contains(b.PointToClient(Cursor.Position)) ? hover : normal; };

            // Optional: Accent border (engine/analysis/danger)
            if (accent.HasValue)
                b.FlatAppearance.BorderColor = accent.Value;
        }

        private void SetButtonActive(WinFormsButton? b, bool active, Color activeAccent)
        {
            if (b is null || !b.Enabled)
                return;

            if (active)
            {
                b.BackColor = Color.FromArgb(55, 55, 55);
                b.FlatAppearance.BorderColor = activeAccent;
                b.ForeColor = Theme.TextPrimary;
            }
            else
            {
                b.BackColor = Theme.ButtonBg;
                b.FlatAppearance.BorderColor = Theme.Border;
                b.ForeColor = Theme.TextPrimary;
            }
        }

        private byte[] CaptureBoardPngBytes()
        {
            // Render board only, current control size
            int w = _board.Width;
            int h = _board.Height;
            if (w <= 0 || h <= 0) return Array.Empty<byte>();

            using var bmp = new Bitmap(w, h);
            _board.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        private static async Task SaveGifAsync(IReadOnlyList<byte[]> pngFrames, string gifPath, int frameDelayMs)
        {
            if (pngFrames.Count == 0) return;

            int delay100 = Math.Max(1, frameDelayMs / 10);

            using var gif = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngFrames[0]);

            var meta = gif.Metadata.GetGifMetadata();
            meta.RepeatCount = 0; // Endless loop

            gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay100;

            for (int i = 1; i < pngFrames.Count; i++)
            {
                using var frameImg = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngFrames[i]);
                frameImg.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay100;
                gif.Frames.AddFrame(frameImg.Frames.RootFrame);
            }

            var enc = new SixLabors.ImageSharp.Formats.Gif.GifEncoder
            {
                ColorTableMode = SixLabors.ImageSharp.Formats.Gif.GifColorTableMode.Global
            };

            await gif.SaveAsync(gifPath, enc).ConfigureAwait(false);
        }

              

        internal sealed class AppConfig
        {
            public string? EngineExePath { get; set; }

            private static string ConfigDir =>
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "chessGUI");

            private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

            public static AppConfig Load()
            {
                try
                {
                    if (!File.Exists(ConfigPath))
                        return new AppConfig();

                    var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    var cfg = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json);
                    return cfg ?? new AppConfig();
                }
                catch
                {
                    return new AppConfig();
                }
            }

            public void Save()
            {
                Directory.CreateDirectory(ConfigDir);
                var json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            }
        }
    }

    internal sealed class UciEngine : IDisposable
    {
        private readonly string _exePath;
        private readonly Action<string> _onLine;
        private Process? _p;
        private StreamWriter? _stdin;
        private Task? _readerTask;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private readonly object _waitLock = new object();
        private string _lastLine = "";
        private readonly ManualResetEventSlim _lineArrived = new ManualResetEventSlim(false);

        public UciEngine(string exePath, Action<string> onLine)
        {
            _exePath = exePath;
            _onLine = onLine;
        }

        public Task StartAsync()
        {
            _p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exePath,
                    WorkingDirectory = Path.GetDirectoryName(_exePath) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                },

                EnableRaisingEvents = true
            };

            _p.Start();
            _stdin = _p.StandardInput;

            _readerTask = Task.Run(() =>
            {
                try
                {
                    while (!_p.HasExited)
                    {
                        var line = _p.StandardOutput.ReadLine();
                        if (line == null) break;
                        lock (_waitLock)
                        {
                            _lastLine = line;
                            _lineArrived.Set();
                        }
                        _onLine(line);
                    }
                }
                catch
                {
                    // bewusst minimal
                }
            });

            return Task.CompletedTask;
        }

        public async Task SendAsync(string cmd)
        {
            if (_stdin == null) return;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stdin.WriteLineAsync(cmd).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task WaitForAsync(string token, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                _lineArrived.Wait(100);
                lock (_waitLock)
                {
                    _lineArrived.Reset();
                    if (_lastLine.Contains(token, StringComparison.Ordinal)) return Task.CompletedTask;
                }
            }
            throw new TimeoutException($"UCI Timeout: {token}");
        }

        public void Dispose()
        {
            try
            {
                if (_p != null && !_p.HasExited) _p.Kill(true);
            }
            catch { }
            finally
            {
                _stdin?.Dispose();
                _p?.Dispose();
                _sendLock.Dispose();
                _lineArrived.Dispose();
            }
        }
    }

    internal sealed class UciInfo
    {
        private static readonly Regex DepthRx = new Regex(@"\bdepth\s+(\d+)\b", RegexOptions.Compiled);
        private static readonly Regex NpsRx = new Regex(@"\bnps\s+(\d+)\b", RegexOptions.Compiled);
        private static readonly Regex ScoreCpRx = new Regex(@"\bscore\s+cp\s+(-?\d+)\b", RegexOptions.Compiled);
        private static readonly Regex ScoreMateRx = new Regex(@"\bscore\s+mate\s+(-?\d+)\b", RegexOptions.Compiled);
        private static readonly Regex PvRx = new Regex(@"\bpv\s+(.+)$", RegexOptions.Compiled);
        private static readonly Regex MultiPvRx = new Regex(@"\bmultipv\s+(\d+)\b", RegexOptions.Compiled);

        public int? Depth { get; private set; }
        public int? Nps { get; private set; }
        public int? ScoreCp { get; private set; }
        public int? ScoreMate { get; private set; }
        public string? Pv { get; private set; }
        public int? MultiPv { get; private set; }

        public static UciInfo? TryParse(string line)
        {
            var info = new UciInfo();

            var m = DepthRx.Match(line);
            if (m.Success) info.Depth = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            m = NpsRx.Match(line);
            if (m.Success) info.Nps = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            m = ScoreCpRx.Match(line);
            if (m.Success) info.ScoreCp = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            m = ScoreMateRx.Match(line);
            if (m.Success) info.ScoreMate = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            m = PvRx.Match(line);
            if (m.Success) info.Pv = m.Groups[1].Value.Trim();

            m = MultiPvRx.Match(line);
            if (m.Success) info.MultiPv = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            if (info.Depth == null && info.ScoreCp == null && info.ScoreMate == null && info.Pv == null) return null;
            return info;
        }

        public string ToDisplayString()
        {
            var sb = new StringBuilder();
            sb.Append("d=").Append(Depth?.ToString(CultureInfo.InvariantCulture) ?? "-");

            if (ScoreMate.HasValue)
                sb.Append("  mate=").Append(ScoreMate.Value.ToString(CultureInfo.InvariantCulture));
            else if (ScoreCp.HasValue)
                sb.Append("  cp=").Append(ScoreCp.Value.ToString(CultureInfo.InvariantCulture));

            if (Nps.HasValue) sb.Append("  nps=").Append(Nps.Value.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrWhiteSpace(Pv)) sb.Append("  pv ").Append(Pv);
            return sb.ToString();
        }
    }

    internal sealed class BoardControl : Control
    {
        public event EventHandler<string>? UciMoveDropped;
        public event EventHandler? DragStarted;
        public event EventHandler? DragEnded;

        private Rectangle _lastDragInvalid = Rectangle.Empty;
        
        private string _fen = "8/8/8/8/8/8/8/8 w - - 0 1";
        public string Fen
        {
            get => _fen;
            set { _fen = value ?? _fen; Invalidate(); }
        }
                
        private string? _dragFrom;
        private bool _isDragging;
        private string? _bestFrom;
        private string? _bestTo;
        private Point _dragPoint;
        private char _dragPiece = '\0';

        private bool _flipped;

        public bool IsFlipped => _flipped;

        public void SetFlipped(bool flipped)
        {
            if (_flipped == flipped) return;
            _flipped = flipped;

            if (_isDragging)
            {
                _isDragging = false;
                _dragFrom = null;
                _dragPiece = '\0';
                Capture = false;
                DragEnded?.Invoke(this, EventArgs.Empty);
            }

            Invalidate();
        }

        private bool _hasDropPreview;
        private string? _dropFrom;
        private string? _dropTo;
        private char _dropPiece;

        public void ClearDropPreview()
        {
            _hasDropPreview = false;
            _dropFrom = null;
            _dropTo = null;
            _dropPiece = '\0';
            Invalidate();
        }

        private int MapIndexForDraw(int i) => _flipped ? (7 - i) : i; 
        private int MapIndexFromMouse(int i) => _flipped ? (7 - i) : i;


        public BoardControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(18, 18, 18);
            ForeColor = Color.White;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public void SetBestMoveArrow(string? from, string? to)
        {
            _bestFrom = from;
            _bestTo = to;
            Invalidate();
        }

        private void DrawBestMoveArrow(Graphics g, Point origin, int cell, bool flipped)
        {
            if (string.IsNullOrWhiteSpace(_bestFrom) || string.IsNullOrWhiteSpace(_bestTo))
                return;

            if (!TrySquareToRc(_bestFrom, out int fr, out int fc)) return;
            if (!TrySquareToRc(_bestTo, out int tr, out int tc)) return;

            // Flip: LOGICAL -> VISUAL coordinates (as in piece drawing)
            if (flipped)
            {
                fr = 7 - fr;
                fc = 7 - fc;
                tr = 7 - tr;
                tc = 7 - tc;
            }

            // Centers of the fields
            float x1 = origin.X + fc * cell + cell / 2f;
            float y1 = origin.Y + fr * cell + cell / 2f;
            float x2 = origin.X + tc * cell + cell / 2f;
            float y2 = origin.Y + tr * cell + cell / 2f;

            float dx = x2 - x1;
            float dy = y2 - y1;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return;

            float ux = dx / len;
            float uy = dy / len;

            // Do not let the arrow run exactly to the center (because of pieces)
            float shortenStart = cell * 0.18f;
            float shortenEnd = cell * 0.28f;

            float sx = x1 + ux * shortenStart;
            float sy = y1 + uy * shortenStart;
            float ex = x2 - ux * shortenEnd;
            float ey = y2 - uy * shortenEnd;

            // Line thickness zoom-stable
            float lineW = Math.Max(2.5f, cell * 0.07f);
            float glowW = lineW * 1.7f;

            var color = Color.FromArgb(210, 0, 90, 0);      // dark green
            var glow = Color.FromArgb(60, 0, 90, 0);        // decent

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            // Glow (optional, helps with light/dark contrast)
            using (var pGlow = new Pen(glow, glowW))
            {
                pGlow.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pGlow.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(pGlow, sx, sy, ex, ey);
            }

            // main line
            using (var p = new Pen(color, lineW))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(p, sx, sy, ex, ey);
            }

            // Arrowhead as triangle (manual, no AdjustableArrowCap)
            float headLen = Math.Max(10f, cell * 0.28f);
            float headW = headLen * 0.65f;

            // The head is at the end (ex, ey), pointing in the direction (ux,uy)
            float hx = ex;
            float hy = ey;

            // Base of the head
            float bx = hx - ux * headLen;
            float by = hy - uy * headLen;

            // Normal vector
            float nx = -uy;
            float ny = ux;

            float lx = bx + nx * (headW / 2f);
            float ly = by + ny * (headW / 2f);
            float rx = bx - nx * (headW / 2f);
            float ry = by - ny * (headW / 2f);

            using (var b = new SolidBrush(color))
            {
                g.FillPolygon(b, new[]
                {
            new PointF(hx, hy),
            new PointF(lx, ly),
            new PointF(rx, ry)
        });
            }
        }


        private static bool TrySquareToRc(string sq, out int r, out int c)
        {
            r = 0; c = 0;
            if (sq.Length != 2) return false;

            char file = sq[0];
            char rank = sq[1];
            if (file < 'a' || file > 'h') return false;
            if (rank < '1' || rank > '8') return false;

            c = file - 'a';
            r = '8' - rank; // rank '8' => r=0
            return true;
        }

        public void SetPosition(ChessPosition pos)
        {
            Fen = pos.ToFen();
            _dragFrom = null;
            _isDragging = false;

            // Remove drop preview because real position is now available
            _hasDropPreview = false;
            _dropFrom = null;
            _dropTo = null;
            _dropPiece = '\0';

            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            var (sq, ok) = PointToSquare(e.Location);
            if (!ok || string.IsNullOrEmpty(sq)) return;

            char piece = GetPieceAtSquareFromFen(sq);
            if (piece == '\0') return;

            _isDragging = true;
            _dragFrom = sq;
            _dragPiece = piece;
            _dragPoint = e.Location;

            _lastDragInvalid = Rectangle.Empty;

            Capture = true;
            Invalidate(); // once is ok

            DragStarted?.Invoke(this, EventArgs.Empty);
        }


        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (!_isDragging) return;

            Capture = false;

            var from = _dragFrom;
            var piece = _dragPiece;

            var (to, ok) = PointToSquare(e.Location);

            // Finish Drag-State
            _isDragging = false;
            _dragFrom = null;
            _dragPiece = '\0';
            _lastDragInvalid = Rectangle.Empty;

            // If drop is valid and field change: Set preview (immediate visual display)
            if (ok && !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to) &&
                !string.Equals(from, to, StringComparison.Ordinal))
            {
                _hasDropPreview = true;
                _dropFrom = from;
                _dropTo = to;
                _dropPiece = piece;
            }
            else
            {
                // No valid move -> Preview off
                _hasDropPreview = false;
                _dropFrom = null;
                _dropTo = null;
                _dropPiece = '\0';
            }

            Invalidate();
            DragEnded?.Invoke(this, EventArgs.Empty);

            if (!ok) return;
            if (string.IsNullOrWhiteSpace(from)) return;
            if (string.Equals(from, to, StringComparison.Ordinal)) return;

            UciMoveDropped?.Invoke(this, from + to);
        }


        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_isDragging) return;

            var old = _lastDragInvalid;

            _dragPoint = e.Location;

            // Board geometry as in OnPaint:
            var boardSize = Math.Min(ClientSize.Width, ClientSize.Height);
            var origin = new Point((ClientSize.Width - boardSize) / 2, (ClientSize.Height - boardSize) / 2);
            var cell = boardSize / 8;

            // New overlay rectangle (with a little margin for outline/AA)
            int pad = Math.Max(4, cell / 10);
            var rectNow = Rectangle.Round(new RectangleF(
                _dragPoint.X - cell / 2f,
                _dragPoint.Y - cell / 2f,
                cell,
                cell));
            rectNow.Inflate(pad, pad);

            _lastDragInvalid = rectNow;

            // Only redraw the affected areas
            if (!old.IsEmpty) Invalidate(old);
            Invalidate(rectNow);
        }



        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);

            if (_isDragging)
            {
                Capture = false;
                _isDragging = false;
                _dragFrom = null;
                _dragPiece = '\0';
                Invalidate();

                DragEnded?.Invoke(this, EventArgs.Empty);
            }
        }


        

        private char GetPieceAtSquareFromFen(string sq)
        {
            try
            {
                // sq: "e2"
                int f = sq[0] - 'a';
                int rank = sq[1] - '0';
                int r = 8 - rank; // r=0 is rank 8

                var placement = Fen.Split(' ')[0];
                var ranks = placement.Split('/');
                if (ranks.Length != 8) return '\0';

                int file = 0;
                foreach (var ch in ranks[r])
                {
                    if (char.IsDigit(ch))
                    {
                        file += (ch - '0');
                    }
                    else
                    {
                        if (file == f) return ch;
                        file++;
                    }
                }
                return '\0';
            }
            catch
            {
                return '\0';
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var boardSize = Math.Min(ClientSize.Width, ClientSize.Height);
            var origin = new Point((ClientSize.Width - boardSize) / 2, (ClientSize.Height - boardSize) / 2);
            var cell = boardSize / 8;

            using var light = new SolidBrush(Color.FromArgb(215, 220, 215)); // warm off-white
            using var dark = new SolidBrush(Color.FromArgb(90, 95, 105));   // cool dark gray

            using var sel = new SolidBrush(Color.FromArgb(120, 80, 160, 255));
            using var pen = new Pen(Color.FromArgb(60, 60, 60), 1);

            // Helper: visual -> logical (bei Flip spiegeln)
            int LR(int vr) => _flipped ? (7 - vr) : vr;  // Logical Rank
            int LC(int vc) => _flipped ? (7 - vc) : vc;  // Logical File/Col

            // squares
            for (int vr = 0; vr < 8; vr++)
            {
                for (int vc = 0; vc < 8; vc++)
                {
                    var isLight = ((vr + vc) % 2 == 0);
                    var rect = new Rectangle(origin.X + vc * cell, origin.Y + vr * cell, cell, cell);

                    e.Graphics.FillRectangle(isLight ? light : dark, rect);
                    e.Graphics.DrawRectangle(pen, rect);

                    // Selection must run via LOGICAL Square
                    var sq = ToSquare(LR(vr), LC(vc));
                    if (_isDragging && _dragFrom == sq)
                        e.Graphics.FillRectangle(sel, rect);
                }
            }

            // Arrow after the fields, before the figures
            // IMPORTANT: DrawBestMoveArrow must also take flip into account.
            DrawBestMoveArrow(e.Graphics, origin, cell, _flipped);

            // pieces (Unicode chess)
            var placement = Fen.Split(' ')[0];
            var ranks = placement.Split('/');
            if (ranks.Length != 8) return;

            using var pieceFont = new Font("Segoe UI Symbol", Math.Max(14, cell * 0.65f), FontStyle.Regular, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // Figuren zeichnen (Startfeld während Drag überspringen)
            for (int r = 0; r < 8; r++) // r is LOGICAL rank (FEN: 0 = 8th rank)
            {
                int file = 0;
                foreach (var ch in ranks[r])
                {
                    if (char.IsDigit(ch))
                    {
                        file += (ch - '0');
                        continue;
                    }

                    // LOGICAL square (fits to FEN)
                    var sq = ToSquare(r, file);

                    // When drag is active: Do not draw the character on the start field (it appears as an overlay on the mouse).
                    if (_isDragging && _dragFrom == sq)
                    {
                        file++;
                        continue;
                    }

                    // If drop preview is active: Suppress start field and target field (preview will be drawn later)
                    if (_hasDropPreview && (_dropFrom == sq || _dropTo == sq))
                    {
                        file++;
                        continue;
                    }


                    // LOGICAL -> VISUAL Position
                    int vr = _flipped ? (7 - r) : r;
                    int vc = _flipped ? (7 - file) : file;

                    var uni = PieceToUnicode(ch);
                    var rect = new Rectangle(origin.X + vc * cell, origin.Y + vr * cell, cell, cell);

                    bool isWhite = char.IsUpper(ch);

                    // Outline only for white pieces
                    if (isWhite)
                    {
                        using var outlineBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
                        float o = 1.4f;

                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y, rect.Width, rect.Height), fmt);
                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y, rect.Width, rect.Height), fmt);
                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X, rect.Y - o, rect.Width, rect.Height), fmt);
                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X, rect.Y + o, rect.Width, rect.Height), fmt);

                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y - o, rect.Width, rect.Height), fmt);
                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y - o, rect.Width, rect.Height), fmt);
                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y + o, rect.Width, rect.Height), fmt);
                        e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y + o, rect.Width, rect.Height), fmt);
                    }

                    using var pieceBrush = new SolidBrush(isWhite ? Color.White : Color.Black);
                    e.Graphics.DrawString(uni, pieceFont, pieceBrush, rect, fmt);

                    file++;
                }
            }

            // Drop preview: Display figure immediately at destination
            if (_hasDropPreview && _dropPiece != '\0' &&
                !string.IsNullOrWhiteSpace(_dropTo) &&
                TrySquareToRc(_dropTo, out int pr, out int pc))
            {
                int vr = _flipped ? (7 - pr) : pr;
                int vc = _flipped ? (7 - pc) : pc;

                var uni = PieceToUnicode(_dropPiece);
                var rect = new Rectangle(origin.X + vc * cell, origin.Y + vr * cell, cell, cell);

                bool isWhite = char.IsUpper(_dropPiece);

                // Outline only for white pieces (as with normal pieces)
                if (isWhite)
                {
                    using var outlineBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
                    float o = 1.4f;

                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X, rect.Y - o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X, rect.Y + o, rect.Width, rect.Height), fmt);

                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y - o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y - o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y + o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y + o, rect.Width, rect.Height), fmt);
                }

                using var pieceBrush = new SolidBrush(isWhite ? Color.White : Color.Black);
                e.Graphics.DrawString(uni, pieceFont, pieceBrush, rect, fmt);
            }


            // Drag overlay: Figure at mouse (CENTERED)Drag overlay: Figure at mouse (CENTERED)
            if (_isDragging && _dragPiece != '\0')
            {
                var uni = PieceToUnicode(_dragPiece);

                // Mouse at the center of the figure
                var rect = new RectangleF(
                    _dragPoint.X - cell / 2f,
                    _dragPoint.Y - cell / 2f,
                    cell,
                    cell);

                bool isWhite = char.IsUpper(_dragPiece);

                if (isWhite)
                {
                    using var outlineBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0));
                    float o = 1.4f;

                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X, rect.Y - o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X, rect.Y + o, rect.Width, rect.Height), fmt);

                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y - o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y - o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X - o, rect.Y + o, rect.Width, rect.Height), fmt);
                    e.Graphics.DrawString(uni, pieceFont, outlineBrush, new RectangleF(rect.X + o, rect.Y + o, rect.Width, rect.Height), fmt);
                }

                using var brush = new SolidBrush(isWhite ? Color.FromArgb(240, 255, 255, 255) : Color.FromArgb(240, 0, 0, 0));
                e.Graphics.DrawString(uni, pieceFont, brush, rect, fmt);
            }

            // Side-Indicator (right bottom)
            DrawSideIndicator(e.Graphics, origin, boardSize);
        }

        private void DrawSideIndicator(Graphics g, Point origin, int boardSize)
        {
            const int size = 13;
            const int gap = 6;     // Distance next to the board

            // to the right of the board, aligned at the bottom
            int x = origin.X + boardSize + gap;
            int y = origin.Y + boardSize - size;

            // If there is no space on the right, fallback: bottom right inside
            if (x + size > ClientSize.Width)
            {
                x = origin.X + boardSize - 4 - size;
                y = origin.Y + boardSize - 4 - size;
            }

            using var fill = new SolidBrush(_flipped ? Color.Black : Color.White);
            using var outline = new Pen(Color.Gold, 2f);

            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            g.FillEllipse(fill, x, y, size, size);
            g.DrawEllipse(outline, x, y, size, size);

            g.SmoothingMode = old;
        }


        private (string? sq, bool ok) PointToSquare(Point p)
        {
            var boardSize = Math.Min(ClientSize.Width, ClientSize.Height);
            var origin = new Point((ClientSize.Width - boardSize) / 2, (ClientSize.Height - boardSize) / 2);
            var cell = boardSize / 8;

            int x = p.X - origin.X;
            int y = p.Y - origin.Y;
            if (x < 0 || y < 0) return (null, false);

            int vc = x / cell;   // visual col 0..7
            int vr = y / cell;   // visual row 0..7
            if (vc < 0 || vc > 7 || vr < 0 || vr > 7) return (null, false);

            // VISUAL -> LOGICAL (flip mirror)
            int lc = _flipped ? (7 - vc) : vc;
            int lr = _flipped ? (7 - vr) : vr;

            // Now convert LOGICAL coordinates to Square (must match your FEN layout)
            return (ToSquare(lr, lc), true);
        }


        private static string ToSquare(int r, int c)
        {
            // r=0 is rank 8
            char file = (char)('a' + c);
            char rank = (char)('8' - r);
            return string.Concat(file, rank);
        }

        private static string PieceToUnicode(char p) => p switch
        {
            'K' => "♔",
            'Q' => "♕",
            'R' => "♖",
            'B' => "♗",
            'N' => "♘",
            'P' => "♙",
            'k' => "♚",
            'q' => "♛",
            'r' => "♜",
            'b' => "♝",
            'n' => "♞",
            'p' => "♟",
            _ => "?"
        };
    }

    internal sealed class ChessPosition
    {
        private readonly char[,] _b = new char[8, 8];
        private char _side = 'w';
        private string _castling = "KQkq";
        private string _ep = "-";
        private int _halfmove = 0;
        private int _fullmove = 1;

        public static ChessPosition FromFen(string fen)
        {
            var p = new ChessPosition();
            p.LoadFen(fen);
            return p;
        }

        public string ToFen()
        {
            var sb = new StringBuilder();

            // 1) Piece placement
            for (int r = 0; r < 8; r++)
            {
                int empty = 0;
                for (int f = 0; f < 8; f++)
                {
                    var c = _b[r, f];
                    if (c == '\0') { empty++; continue; }
                    if (empty > 0) { sb.Append(empty); empty = 0; }
                    sb.Append(c);
                }
                if (empty > 0) sb.Append(empty);
                if (r != 7) sb.Append('/');
            }

            // 2) Side to move
            sb.Append(' ').Append(_side);

            // 3) Castling
            sb.Append(' ').Append(string.IsNullOrWhiteSpace(_castling) ? "-" : _castling);

            // 4) EP (X-FEN/lichess/Stockfish-style): Only output if Side-to-move can really beat ep.
            string epOut = ComputeTrueEpForOutput();
            sb.Append(' ').Append(epOut);

            // 5) halfmove / fullmove
            sb.Append(' ').Append(_halfmove.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ').Append(_fullmove.ToString(CultureInfo.InvariantCulture));

            return sb.ToString();
        }

        private string ComputeTrueEpForOutput()
        {
            // If no ep is set internally -> “-”
            if (string.IsNullOrWhiteSpace(_ep) || _ep == "-")
                return "-";

            // EP-Square must be valid (e.g., “e3” or “d6”)
            if (_ep.Length != 2)
                return "-";

            int file = _ep[0] - 'a';
            int rank = _ep[1] - '0'; // 1..8
            if (file < 0 || file > 7 || rank < 1 || rank > 8)
                return "-";

            // internal indices: r=0 is rank 8, r=7 is rank 1
            int epR = 8 - rank;   // EP-Zielfeld in Board-Indizes

            // For “true ep,” the pseudo-legality check is sufficient:
            // Side-to-move must have a pawn that could capture from the left/right on epR, file.
            char pawn = (_side == 'w') ? 'P' : 'p';

            // White can only capture ep on rank 6 (epR=2), while his pawns are on rank 5 (r=3).
            // Black can only capture ep on rank 3 (epR=5), while his pawns are on rank 4 (r=4).
            int pawnR = (_side == 'w') ? epR + 1 : epR - 1;
            if (pawnR < 0 || pawnR > 7)
                return "-";

            bool canCapture =
                (file > 0 && _b[pawnR, file - 1] == pawn) ||
                (file < 7 && _b[pawnR, file + 1] == pawn);

            return canCapture ? _ep : "-";
        }


        public void LoadFen(string fen)
        {
            Array.Clear(_b, 0, _b.Length);

            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var placement = parts[0];
            _side = parts.Length > 1 ? parts[1][0] : 'w';
            _castling = parts.Length > 2 ? parts[2] : "-";
            _ep = parts.Length > 3 ? parts[3] : "-";
            _halfmove = parts.Length > 4 ? int.Parse(parts[4], CultureInfo.InvariantCulture) : 0;
            _fullmove = parts.Length > 5 ? int.Parse(parts[5], CultureInfo.InvariantCulture) : 1;

            var ranks = placement.Split('/');
            for (int r = 0; r < 8; r++)
            {
                int f = 0;
                foreach (var ch in ranks[r])
                {
                    if (char.IsDigit(ch)) f += (ch - '0');
                    else { _b[r, f] = ch; f++; }
                }
            }
            if (_castling == "-") _castling = "";
        }
        
        public bool TryApplyUciMove(string uci)
        {
            if (string.IsNullOrWhiteSpace(uci) || (uci.Length != 4 && uci.Length != 5)) return false;

            var from = uci.Substring(0, 2);
            var to = uci.Substring(2, 2);
            var promo = uci.Length == 5 ? uci[4] : '\0';

            var (fr, ff) = SqToRf(from);
            var (tr, tf) = SqToRf(to);

            var piece = _b[fr, ff];
            if (piece == '\0') return false;

            if (_side == 'w' && !char.IsUpper(piece)) return false;
            if (_side == 'b' && !char.IsLower(piece)) return false;

            // notrice Capture (for halfmove)
            bool isCapture = _b[tr, tf] != '\0';

            _b[fr, ff] = '\0';

            // En-passant minimal
            if ((piece == 'P' || piece == 'p') && _ep != "-" && _ep == to && _b[tr, tf] == '\0')
            {
                if (piece == 'P') _b[tr + 1, tf] = '\0';
                else _b[tr - 1, tf] = '\0';
                isCapture = true;
            }

            // castling minimal
            if (piece == 'K' && from == "e1" && to == "g1") { MovePiece("h1", "f1"); RemoveCastling('K', 'Q'); }
            if (piece == 'K' && from == "e1" && to == "c1") { MovePiece("a1", "d1"); RemoveCastling('K', 'Q'); }
            if (piece == 'k' && from == "e8" && to == "g8") { MovePiece("h8", "f8"); RemoveCastling('k', 'q'); }
            if (piece == 'k' && from == "e8" && to == "c8") { MovePiece("a8", "d8"); RemoveCastling('k', 'q'); }

            // Promotion
            if ((piece == 'P' && tr == 0) || (piece == 'p' && tr == 7))
            {
                if (promo == '\0') promo = 'q';
                piece = _side == 'w' ? char.ToUpperInvariant(promo) : char.ToLowerInvariant(promo);
            }

            _b[tr, tf] = piece;

            // set EP only, if the opponent can take it ep
            _ep = "-";

            if (piece == 'P' && fr == 6 && tr == 4)
            {
                // White pawn double push to rank 4 -> ep target is file + "3"
                string ep = $"{(char)('a' + ff)}3";

                bool blackCanCaptureEp = false;
                // black pawn must be on rank 4 (same rank as the pawn after move), adjacent file
                if (ff > 0 && _b[tr, ff - 1] == 'p') blackCanCaptureEp = true;
                if (ff < 7 && _b[tr, ff + 1] == 'p') blackCanCaptureEp = true;

                _ep = blackCanCaptureEp ? ep : "-";
            }
            else if (piece == 'p' && fr == 1 && tr == 3)
            {
                // Black pawn double push to rank 5 -> ep target is file + "6"
                string ep = $"{(char)('a' + ff)}6";

                bool whiteCanCaptureEp = false;
                // white pawn must be on rank 5 (same rank as the pawn after move), adjacent file
                if (ff > 0 && _b[tr, ff - 1] == 'P') whiteCanCaptureEp = true;
                if (ff < 7 && _b[tr, ff + 1] == 'P') whiteCanCaptureEp = true;

                _ep = whiteCanCaptureEp ? ep : "-";
            }

            // halfmove/fullmove minimal
            _halfmove++;
            if (piece == 'P' || piece == 'p' || isCapture) _halfmove = 0;

            if (_side == 'b') _fullmove++;
            _side = (_side == 'w') ? 'b' : 'w';
            return true;
        }

        private void RemoveCastling(params char[] flags)
        {
            foreach (var f in flags) _castling = _castling.Replace(f.ToString(), "");
            if (_castling == "-") _castling = "";
        }

        private void MovePiece(string from, string to)
        {
            var (fr, ff) = SqToRf(from);
            var (tr, tf) = SqToRf(to);
            _b[tr, tf] = _b[fr, ff];
            _b[fr, ff] = '\0';
        }

        private static (int r, int f) SqToRf(string sq)
        {
            int f = sq[0] - 'a';
            int rank = sq[1] - '0';
            int r = 8 - rank;
            return (r, f);
        }
    }

    internal static class PgnMainlineExtractor
    {
        public static List<string> ExtractMainlineSan(string pgn)
        {
            // 1) Remove Tag Pairs
            var sb = new StringBuilder();
            using (var sr = new StringReader(pgn))
            {
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.StartsWith("[", StringComparison.Ordinal)) continue;
                    sb.AppendLine(line);
                }
            }

            string s = sb.ToString();

            // 2) Remove comments
            s = Regex.Replace(s, @";[^\r\n]*", " ");

            // 3) Remove comments
            s = Regex.Replace(s, @"\{[^}]*\}", " ");

            // 4) 
            s = RemoveParenthesesBlocks(s);

            // 5) Remove NAG $123
            s = Regex.Replace(s, @"\$\d+", " ");

            // 6) Remove symbols
            s = Regex.Replace(s, @"(\!\?|\?\!|\!\!|\?\?|\!|\?)", " ");

            // 7) Remove Result-Tokens
            s = Regex.Replace(s, @"\b(1-0|0-1|1/2-1/2|\*)\b", " ");

            // 8) Remove Move Numbers 
            s = Regex.Replace(s, @"\b\d+\.(\.\.)?\b", " ");

            // 9) Tokenize
            var tokens = s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var moves = new List<string>();
            foreach (var t in tokens)
            {
                // SAN rough recognition: castling or contains letters/numbers
                // Gera.Chess performs the execution/validation during import.
                if (t == "O-O" || t == "O-O-O" || Regex.IsMatch(t, @"^[KQRBN]?[a-h]?[1-8]?x?[a-h][1-8](=[QRBN])?[\+#]?$") || Regex.IsMatch(t, @"^[a-h]x?[a-h][1-8](=[QRBN])?[\+#]?$"))
                {
                    moves.Add(t);
                }
            }

            return moves;
        }

        private static string RemoveParenthesesBlocks(string s)
        {
            var sb = new StringBuilder(s.Length);
            int depth = 0;
            foreach (char c in s)
            {
                if (c == '(') { depth++; continue; }
                if (c == ')') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(c);
            }
            return sb.ToString();
        }
    }

    
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(24, 24, 24);
        public static readonly Color PanelBg = Color.FromArgb(20, 20, 20);

        public static readonly Color ButtonBg = Color.FromArgb(44, 44, 44);
        public static readonly Color ButtonHover = Color.FromArgb(60, 60, 60);
        public static readonly Color ButtonDown = Color.FromArgb(36, 36, 36);

        public static readonly Color TextPrimary = Color.FromArgb(225, 225, 225);

        public static readonly Color AccentEngine = Color.FromArgb(70, 130, 180);   // SteelBlue
        public static readonly Color AccentAnalyse = Color.FromArgb(70, 160, 90);   // Green
        public static readonly Color Danger = Color.FromArgb(200, 70, 70);          // Red

        public static readonly Color Border = Color.FromArgb(90, 90, 90);
        public static readonly Color Separator = Color.FromArgb(55, 55, 55);
    }

    internal static class FileLog
    {
        private static readonly object _lock = new object();
        private static string? _path;

        public static string? PathOrNull => _path;

        public static void Init(string appName)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                appName,
                "logs");

            Directory.CreateDirectory(dir);

            _path = Path.Combine(dir, $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            Write("=== Log start ===");
            Write($"Process: {Environment.ProcessPath}");
            Write($"OS: {Environment.OSVersion}");
            Write($"64bit: {Environment.Is64BitProcess}");
        }

        public static void Write(string msg)
        {
            if (_path == null) return;

            var line = $"{DateTime.Now:HH:mm:ss.fff}  {msg}";
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
    }

    public sealed class FenInputDialog : Form
    {
        private readonly System.Windows.Forms.TextBox _tb = new System.Windows.Forms.TextBox();
        public string FenText { get => _tb.Text; set => _tb.Text = value ?? ""; }

        public FenInputDialog()
        {
            Text = "Enter FEN";
            Width = 900;
            Height = 220;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;

            _tb.Dock = DockStyle.Top;
            _tb.Multiline = true;
            _tb.Height = 80;
            _tb.Font = new Font("Consolas", 10f);
            _tb.ScrollBars = ScrollBars.Vertical;

            var info = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Text = "FEN:",
            };

            var panelButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10),
                WrapContents = false
            };

            var ok = new System.Windows.Forms.Button { Text = "OK", DialogResult = DialogResult.OK, Width = 130, Height = 34, Margin = new Padding(8, 8, 0, 8) };
            var cancel = new System.Windows.Forms.Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 130, Height = 34, Margin = new Padding(8, 8, 0, 8) };
            ok.Anchor = AnchorStyles.Right;
            cancel.Anchor = AnchorStyles.Right;

            AcceptButton = ok;
            CancelButton = cancel;

            panelButtons.Controls.Add(ok);
            panelButtons.Controls.Add(cancel);

            Controls.Add(panelButtons);
            Controls.Add(_tb);
            Controls.Add(info);
        }
    }

    public sealed class PromotionDialog : Form
    {
        public char SelectedPromo { get; private set; } = '\0';

        public PromotionDialog(bool white)
        {
            Text = "Promotion";

            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;

            Padding = new Padding(12);

            var info = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "Select the piece for promotion:"
            };

            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(6),
                Margin = new Padding(0)
            };

            panel.Controls.Add(MakeButton(white ? 'Q' : 'q', "Queen"));
            panel.Controls.Add(MakeButton(white ? 'R' : 'r', "Rook"));
            panel.Controls.Add(MakeButton(white ? 'B' : 'b', "Bishop"));
            panel.Controls.Add(MakeButton(white ? 'N' : 'n', "Knight"));

            var host = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            host.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            host.Controls.Add(info, 0, 0);
            host.Controls.Add(panel, 0, 1);

            Controls.Add(host);

            // Sicherheit: minimale Größe, damit nie unten abgeschnitten wird
            MinimumSize = new Size(560, 220);
        }


        private WinFormsButton MakeButton(char promo, string text)
        {
            string uni = promo switch
            {
                'Q' => "♕",
                'R' => "♖",
                'B' => "♗",
                'N' => "♘",
                'q' => "♛",
                'r' => "♜",
                'b' => "♝",
                'n' => "♞",
                _ => "?"
            };

            var b = new WinFormsButton
            {
                Width = 110,
                Height = 70,
                Text = $"{uni}\n{text}",
                Font = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point),
                TextAlign = ContentAlignment.MiddleCenter
            };

            b.Click += (_, __) =>
            {
                SelectedPromo = promo;
                DialogResult = DialogResult.OK;
                Close();
            };

            return b;
        }
    }
}

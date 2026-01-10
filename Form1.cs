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


namespace chessGUI
{
    public partial class Form1 : Form
    {
        private readonly Panel _boardHost = new Panel();
        private readonly BoardControl _board = new BoardControl();
        private readonly WinFormsTrackBar _zoom = new WinFormsTrackBar(); 
        private readonly WinFormsTextBox _analysis = new WinFormsTextBox();

        private ChessPosition _pos;
        private readonly List<string> _fenHistory = new List<string>();
        private int _historyIndex = 0;

        private bool _suppressMovesSelectionChanged = false;

        private readonly List<string> _moveHistory = new List<string>(); // UCI-Moves, Länge = _fenHistory.Count - 1
        private readonly ListBox _moves = new ListBox();

        private UciEngine? _engine;
        private CancellationTokenSource? _analysisCts;

        private string _currentFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        private string? _engineExePath;

        private readonly object _fenProbeLock = new object();
        private TaskCompletionSource<string>? _fenProbeTcs;

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

        private bool _boardFlipped = false;
        

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
                _suppressEngineUi = true;          // UI-Updates von Engine aus
                await StopAnalysisImmediatelyAsync(); // Analyse wirklich stoppen
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
            _btnStop.Click += async (_, __) => await StopAnalysisAndEngineAsync();
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

            // Move-Liste (unten)
            _moves.Dock = DockStyle.Fill;
            _moves.Font = new Font("Consolas", 11f, FontStyle.Bold);
            _moves.BackColor = Color.FromArgb(16, 16, 16);
            _moves.ForeColor = Color.Gainsboro;
            _moves.BorderStyle = BorderStyle.FixedSingle;
            _moves.IntegralHeight = false;
            _moves.SelectionMode = SelectionMode.One;

            _moves.SelectedIndexChanged += async (_, __) =>
            {
                if (_suppressMovesSelectionChanged) return;

                if (_moves.SelectedIndex < 0) return;

                int targetHistoryIndex = _moves.SelectedIndex + 1; // moves[0] gehört zu fenHistory[1]
                if (targetHistoryIndex == _historyIndex) return;

                _historyIndex = targetHistoryIndex;
                _pos.LoadFen(_fenHistory[_historyIndex]);
                _board.SetPosition(_pos);

                RefreshMovesUI();
                UpdateNavButtons();
                await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
            };

            // Split: oben Analyse, unten Moves
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

            // Buttons besser lesbar
            var btnText = Color.Gold;      // alternativ: Color.White
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
                // Wichtig: Analyse sofort stoppen passiert in TryApplyUserMove...
                var ok = await TryApplyUserMoveViaEngineAsync(uciMove);
                if (!ok)
                {
                    AppendAnalysisLine($"[GUI] illegal/abgelehnt: {uciMove}");
                    BeepIllegalMove();
                }
                    
            };

            UpdateNavButtons();
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

            bool canBack = _historyIndex > 0;
            bool canFwd = _historyIndex < _fenHistory.Count - 1;

            if (_btnFirst != null) _btnFirst.Enabled = canBack;
            if (_btnBack != null) _btnBack.Enabled = canBack;

            if (_btnForward != null) _btnForward.Enabled = canFwd;
            if (_btnLast != null) _btnLast.Enabled = canFwd;
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

            await _engine.SendAsync("setoption name Threads value 7").ConfigureAwait(false);
            await _engine.SendAsync("setoption name MultiPV value 3").ConfigureAwait(false);
            await _engine.SendAsync("setoption name Hash value 1600").ConfigureAwait(false);

            await _engine.SendAsync("isready").ConfigureAwait(false);
            await _engine.WaitForAsync("readyok", TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            AppendAnalysisLine("[GUI] Engine Optionen: Threads=7, MultiPV=3, Hash=1600");
        }

        private static string UciToSanOrUciFallback(string prevFen, string uciMove)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(prevFen)) return uciMove;
                if (string.IsNullOrWhiteSpace(uciMove) || (uciMove.Length != 4 && uciMove.Length != 5)) return uciMove;

                string from = uciMove.Substring(0, 2);
                string to = uciMove.Substring(2, 2);

                // Gera.Chess kann aus FEN laden und Move-Objekte ausführen. :contentReference[oaicite:2]{index=2}
                var b = ChessBoard.LoadFromFen(prevFen);

                // Promotion (uci.Length==5) lassen wir erstmal als Fallback laufen,
                // bis wir es sauber über eine passende Move-Overload abbilden.
                if (uciMove.Length == 5)
                    return uciMove;

                b.Move(new Move(from, to));

                // SAN aus PGN holen: letztes Zug-Token extrahieren
                string pgn = b.ToPgn();

                // Ergebnis entfernen
                pgn = Regex.Replace(pgn, @"\b(1-0|0-1|1/2-1/2|\*)\b", " ").Trim();

                // Tokenize und letztes "Move"-Token nehmen
                var tokens = pgn.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                string last = "";
                for (int i = tokens.Length - 1; i >= 0; i--)
                {
                    string t = tokens[i];

                    // Skip Move-Nummern wie "12." oder "12..."
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

            AppendAnalysisLine($"[GUI] Engine gesetzt: {_engineExePath}");

            // Optional: falls Engine gerade läuft -> sauber neu starten
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

            try
            {
                await _engine.SendAsync("stop").ConfigureAwait(false);
            }
            catch
            {
                // bewusst minimal
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

            using var ofd = new OpenFileDialog
            {
                Title = "PGN Datei importieren (Hauptlinie)",
                Filter = "PGN (*.pgn)|*.pgn|Alle Dateien (*.*)|*.*",
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
                MessageBox.Show("Keine Züge in der PGN gefunden (nach Entfernen von Varianten/Kommentaren).",
                    "PGN Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Analyse stoppen
            if (_engine != null && _analysisCts != null)
                await _engine.SendAsync("stop").ConfigureAwait(false);

            // Alles auf Start zurück (History leer)
            await NewGameAsync().ConfigureAwait(false);

            // Gera.Chess: SAN ausführen + FEN nach jedem Halbzug sammeln :contentReference[oaicite:2]{index=2}
            var board = new ChessBoard(); // Startpos

            // wir bauen History komplett neu (Startstellung + n Halbzüge)
            _fenHistory.Clear();
            _moveHistory.Clear();
            _historyIndex = 0;

            // Start-FEN aus Gera.Chess holen -> in unser Modell übernehmen
            string startFen = board.ToFen(); // vorhanden laut Gera.Chess Doku :contentReference[oaicite:3]{index=3}
            _pos.LoadFen(startFen);
            _fenHistory.Add(_pos.ToFen());

            int importedPlies = 0;

            foreach (var san in sanMoves)
            {
                try
                {
                    board.Move(san); // SAN ausführen :contentReference[oaicite:4]{index=4}
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"PGN Import abgebrochen bei Zug:\n{san}\n\n{ex.Message}",
                        "PGN Import", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                }

                string fen = board.ToFen();
                _pos.LoadFen(fen);

                _moveHistory.Add(san);      // Anzeige: SAN (passt zu PGN)
                _fenHistory.Add(_pos.ToFen());
                _historyIndex = _fenHistory.Count - 1;
                importedPlies++;
            }

            _board.SetPosition(_pos);
            RefreshMovesUI();
            UpdateNavButtons();

            AppendAnalysisLine($"[GUI] PGN importiert: {importedPlies} Halbzüge (Hauptlinie).");

            await StartEngineAsync().ConfigureAwait(false);          // falls noch nicht gestartet
            await ApplyEngineOptionsAsync().ConfigureAwait(false);   // Optionen sicher setzen
            await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
        }

        private void ExportPgn()
        {
            // Startstellung alleine exportieren macht wenig Sinn, aber erlauben wir.
            using var sfd = new SaveFileDialog
            {
                Title = "PGN speichern",
                Filter = "PGN (*.pgn)|*.pgn|Alle Dateien (*.*)|*.*",
                FileName = "game.pgn",
                OverwritePrompt = true
            };

            if (sfd.ShowDialog(this) != DialogResult.OK)
                return;

            string pgn = BuildPgnFromSanHistory();

            File.WriteAllText(sfd.FileName, pgn, Encoding.UTF8);
            AppendAnalysisLine($"[GUI] PGN exportiert: {sfd.FileName}");
        }

        private string BuildPgnFromSanHistory()
        {
            // Minimal-Header (kann später erweitert werden)
            var sb = new StringBuilder();
            sb.AppendLine("[Event \"chessGUI\"]");
            sb.AppendLine("[Site \"Local\"]");
            sb.AppendLine($"[Date \"{DateTime.Now:yyyy.MM.dd}\"]");
            sb.AppendLine("[Round \"-\"]");
            sb.AppendLine("[White \"-\"]");
            sb.AppendLine("[Black \"-\"]");
            sb.AppendLine("[Result \"*\"]");
            sb.AppendLine();

            // Moves: _moveHistory enthält SAN (von dir erzeugt oder beim Import)
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
                AppendAnalysisLine("[GUI] Autoplay abgebrochen (kein GIF-Ziel gewählt).");
                return;
            }

            _autoplayRunning = true;
            _autoplayCts = new CancellationTokenSource();
            await GoToStartAsync(); // immer bei Zug 1 starten

            FileLog.Write($"Autoplay START: historyIndex={_historyIndex} / last={(_fenHistory.Count - 1)}");

            UI(() => { if (_btnAutoplay != null) _btnAutoplay.Text = "Stop Play"; });
            UI(() => AppendAnalysisLine("[GUI] Autoplay startet..."));

            try
            {
                await StartAnalysisAsync(); // UI-Thread

                var frames = new List<byte[]>();

                // Frame 0: aktuelle Stellung (Start / aktueller Index)
                frames.Add(CaptureBoardPngBytes());

                while (_historyIndex < _fenHistory.Count - 1)
                {
                    _autoplayCts.Token.ThrowIfCancellationRequested();

                    await GoForwardAsync(); // UI-safe

                    // nach dem Vorwärtsschritt Frame aufnehmen
                    frames.Add(CaptureBoardPngBytes());

                    await Task.Delay(FrameMs, _autoplayCts.Token);
                }

                UI(() => AppendAnalysisLine("[GUI] Autoplay fertig (Endstellung)."));
                FileLog.Write($"GIF: encoding frames={frames.Count} -> {_gifOutputPath}");

                if (!string.IsNullOrWhiteSpace(_gifOutputPath))
                {
                    // Encoding bewusst nicht auf UI-Thread
                    string path = _gifOutputPath;
                    await Task.Run(async () => await SaveGifAsync(frames, path, FrameMs).ConfigureAwait(false)).ConfigureAwait(false);
                    UI(() => AppendAnalysisLine($"[GUI] GIF gespeichert: {_gifOutputPath}"));
                    FileLog.Write($"GIF: saved {path}");
                }

            }
            catch (OperationCanceledException)
            {
                AppendAnalysisLine("[GUI] Autoplay gestoppt.");
                FileLog.Write("Autoplay CANCELED.");
            }
            catch (Exception ex)
            {
                AppendAnalysisLine("[GUI] Autoplay FEHLER: " + ex.GetType().Name + " - " + ex.Message);
                FileLog.Write("Autoplay ERROR: " + ex);
                MessageBox.Show(ex.ToString(), "Autoplay Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                Title = "GIF speichern",
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

            // Nur wenn Analyse läuft und Engine da ist
            if (_engine == null || _analysisCts == null)
                return;

            // Coalesce: nur 1 Worker gleichzeitig starten
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

                            // Engine Update: stop -> position -> go infinite
                            await _engine.SendAsync("stop").ConfigureAwait(false);
                            await _engine.SendAsync($"position fen {f}").ConfigureAwait(false);
                            await _engine.SendAsync("go infinite").ConfigureAwait(false);

                            // Falls währenddessen schon wieder ein neuer Fen-Wunsch kam: loop
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

                    // Falls während Reset jemand neuen Fen gesetzt hat, direkt nochmal anstoßen
                    if (_pendingAnalysisFen != null && _engine != null && _analysisCts != null)
                        RequestAnalysisUpdate(_pendingAnalysisFen);
                }
            });
        }



        private async Task NewGameAsync()
        {
            StopAutoplay();

            // Analyse stoppen (falls läuft)
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

            // WICHTIG für deinen modifizierten Stockfish: position startpos senden
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

            // toleranter machen: Mehrfach-Spaces reduzieren
            fen = Regex.Replace(fen, @"\s+", " ");

            // Viele User haben nur 4 Felder (ohne halfmove/fullmove) – UCI erwartet meist 6 -> auffüllen
            fen = EnsureFen6Fields(fen);

            // Validieren: dein ChessPosition kann FEN laden (wir nutzen try/catch als Validator)
            try
            {
                var tmp = ChessPosition.FromFen(fen);  // validiert
                fen = tmp.ToFen();                     // kanonisieren (optional, aber praktisch)
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

            // Minimal: 4 Felder sind üblich (placement, side, castling, ep)
            if (parts.Length == 4)
                return fen + " 0 1";

            // 5 Felder -> fullmove fehlt
            if (parts.Length == 5)
                return fen + " 1";

            return fen; // lassen, Validation fängt dann ab
        }

        private async Task LoadFenAsNewGameAsync(string fen)
        {
            // Analyse stoppen (falls läuft)
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

            // Engine auf diese Stellung setzen (statt position startpos) :contentReference[oaicite:5]{index=5}
            if (_engine != null)
            {
                await ApplyEngineOptionsAsync().ConfigureAwait(false); // setzt Threads/MultiPV/Hash + readyok :contentReference[oaicite:6]{index=6}
                await _engine.SendAsync($"position fen {_pos.ToFen()}").ConfigureAwait(false);
            }

            _board.SetBestMoveArrow(null, null);

            // Falls Analyse läuft: direkt neu anstoßen (dein Coalesce-Mechanismus macht das sauber) :contentReference[oaicite:7]{index=7}
            await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
        }

        private async Task StopAnalysisIfRunningAsync()
        {
            if (_engine == null) return;
            if (_analysisCts == null) return;

            await _engine.SendAsync("stop").ConfigureAwait(false);
        }

        private async Task GoFirstAsync()
        {
            if (_historyIndex <= 0) return;

            _historyIndex = 0;
            _pos.LoadFen(_fenHistory[_historyIndex]);
            _board.SetPosition(_pos);
            RefreshMovesUI();
            UpdateNavButtons();
            await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
           
        }

        private async Task GoLastAsync()
        {
            if (_historyIndex >= _fenHistory.Count - 1) return;

            _historyIndex = _fenHistory.Count - 1;
            _pos.LoadFen(_fenHistory[_historyIndex]);
            _board.SetPosition(_pos);
            RefreshMovesUI();
            UpdateNavButtons();
            await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
            
        }
        private async Task<bool> TryApplyUserMoveViaEngineAsync(string uciMove)
        {
            if (_engine == null)
            {
                await StartEngineAsync();
                if (_engine == null) return false;
            }

            // Wenn Analyse läuft: sofort stoppen (wie gehabt)
            if (_analysisCts != null)
                await _engine.SendAsync("stop").ConfigureAwait(false);

            string prevFenForSan = _pos.ToFen();

            // 1) Legalität über Engine (UCI-standard, funktioniert auch bei Berserk)
            bool legal = await IsUciMoveLegalByEngineAsync(prevFenForSan, uciMove).ConfigureAwait(false);
            if (!legal)
            {
                AppendAnalysisLine($"[GUI] illegal/abgelehnt: {uciMove}");
                BeepIllegalMove();

                await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
                return false;
            }

            // 2) Nächste Stellung sauber über Gera.Chess erzeugen (FEN inkl. Castling/EP/50-move korrekt)
            if (!TryApplyUciWithGeraChess(prevFenForSan, uciMove, out string nextFen))
            {
                // Sollte bei legal==true praktisch nicht passieren, aber sicher ist sicher
                AppendAnalysisLine($"[GUI] illegal/abgelehnt (gera-fail): {uciMove}");
                await RestartAnalysisIfRunningAsync().ConfigureAwait(false);
                return false;
            }

            // neue Stellung übernehmen
            _pos.LoadFen(nextFen);
            _currentFen = _pos.ToFen();

            // WICHTIG: wenn nicht am Ende -> Zukunft abschneiden (überschreiben)
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
            RefreshMovesUI();

            _board.SetPosition(_pos);
            UpdateNavButtons();

            // Analyse wieder anstoßen (dein Throttle-Mechanismus)
            RequestAnalysisUpdate(_pos.ToFen());

            AppendAnalysisLine($"[GUI] angewendet: {uciMove}  FEN: {_pos.ToFen()}");
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

                var board = ChessBoard.LoadFromFen(prevFen);

                // Promotion (optional)
                Move move;
                if (uciMove.Length == 5)
                {
                    char promo = uciMove[4]; // q r b n

                    // Manche Gera.Chess-Versionen haben Move(from,to,char), manche nicht.
                    var ctor = typeof(Move).GetConstructor(new[] { typeof(string), typeof(string), typeof(char) });
                    if (ctor == null) return false;

                    move = (Move)ctor.Invoke(new object[] { from, to, promo });
                }
                else
                {
                    move = new Move(from, to);
                }

                // Wir verlassen uns darauf, dass Gera.Chess hier illegalen Zug wirft -> false
                board.Move(move);

                nextFen = board.ToFen();
                return true;
            }
            catch
            {
                return false;
            }
        }
                
        private void ApplyZoom()
        {
            // TrackBar 40..140 -> Brettgröße etwas kompakter (linear)
            var size = (int)Math.Round(_zoom.Value / 100.0 * 640);
            size = Math.Max(300, Math.Min(860, size));

            _boardHost.Width = size + 24; // Padding
            _board.Size = new Size(size, size);
            _board.Invalidate();
        }

        private async Task StartEngineAsync()
        {
            if (_engine != null) return;

            // 1) Pfad laden/prüfen, sonst auswählen
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

            // 2) Engine starten (WorkingDirectory = Engine-Ordner => Textfiles dort)
            _engine = new UciEngine(_engineExePath, HandleEngineLine);

            await _engine.StartAsync().ConfigureAwait(false);

            await _engine.SendAsync("uci").ConfigureAwait(false);
            await _engine.WaitForAsync("uciok", TimeSpan.FromSeconds(3)).ConfigureAwait(false);

            await ApplyEngineOptionsAsync().ConfigureAwait(false);

            AppendAnalysisLine("[GUI] Engine bereit.");

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

            SetButtonActive(_btnAnalyse, true, Theme.AccentAnalyse);

            _analysis.Clear();
            AppendAnalysisLine("[GUI] Analyse startet...");

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
            catch { /* bewusst minimal */ }
            finally
            {
                _analysisCts?.Cancel();
                _analysisCts = null;

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
                System.Media.SystemSounds.Beep.Play(); // alternativ: SystemSounds.Hand / Exclamation / Asterisk
            }
            catch
            {
                // bewusst minimal
            }
        }


        private void HandleEngineLine(string line)
        {
            if (_suppressEngineUi)
            {
                // Wichtig: bestmove-Probe trotzdem zulassen (Legalitätscheck)
                if (!line.StartsWith("bestmove ", StringComparison.Ordinal))
                    return;
            }

            // 1) bestmove-Probe für searchmoves (Legalitätscheck) – thread-safe
            if (line.StartsWith("bestmove ", StringComparison.Ordinal))
            {
                TaskCompletionSource<string>? tcs = null;
                lock (_bestmoveProbeLock)
                {
                    tcs = _bestmoveProbeTcs;
                    _bestmoveProbeTcs = null;
                }
                tcs?.TrySetResult(line);
            }

            // 2) Info-Zeilen: parsing im Reader-Thread, UI nur gedrosselt
            if (line.StartsWith("info ", StringComparison.Ordinal))
            {
                var parsed = UciInfo.TryParse(line);
                if (parsed == null) return;

                // nur PV1
                if (parsed.MultiPv.HasValue && parsed.MultiPv.Value != 1) return;

                long now = _uiThrottleSw.ElapsedMilliseconds;
                if (now < _nextUiUpdateMs) return;
                _nextUiUpdateMs = now + 200;

                // Arrow nur bei neuer Depth
                if (parsed.Depth.HasValue && parsed.Depth.Value != _lastDepthForArrow)
                {
                    _lastDepthForArrow = parsed.Depth.Value;

                    if (!string.IsNullOrWhiteSpace(parsed.Pv))
                    {
                        var first = parsed.Pv.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (first.Length > 0 && first[0].Length >= 4)
                        {
                            string mv = first[0];
                            string from = mv.Substring(0, 2);
                            string to = mv.Substring(2, 2);

                            UI(() => _board.SetBestMoveArrow(from, to));
                        }
                    }
                }

                // Log nur bei neuer Depth
                if (parsed.Depth.HasValue && parsed.Depth.Value != _lastDepthForLog)
                {
                    _lastDepthForLog = parsed.Depth.Value;
                    var txt = parsed.ToDisplayString();
                    UI(() => AppendAnalysisLine(txt));
                }

                return;
            }

            // 3) bestmove normal anzeigen (UI)
            if (line.StartsWith("bestmove ", StringComparison.Ordinal))
            {
                var s = line.Trim();
                UI(() => AppendAnalysisLine(s));
            }
        }


        // ====== NEU: Legality-Check über UCI-standard "searchmoves" ======
        private async Task<bool> IsUciMoveLegalByEngineAsync(string prevFen, string uciMove)
        {
            if (_engine == null) return false;

            // bestmove-Antwort einsammeln
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_bestmoveProbeLock) { _bestmoveProbeTcs = tcs; }

            // Wichtig: Position setzen und nur diesen einen Zug zulassen
            await _engine.SendAsync($"position fen {prevFen}").ConfigureAwait(false);
            await _engine.SendAsync($"go depth 1 searchmoves {uciMove}").ConfigureAwait(false);

            using var cts = new CancellationTokenSource(1500);
            try
            {
                var line = await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                // Beispiele:
                // bestmove e2e4 ponder ...
                // bestmove 0000
                // bestmove (none)
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return false;

                var best = parts[1].Trim();
                return string.Equals(best, uciMove, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                lock (_bestmoveProbeLock) { _bestmoveProbeTcs = null; }
                return false;
            }
        }

        private static string NormalizeFen4(string fen)
        {
            // nur die stabilen Felder vergleichen: placement side castling ep
            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return fen.Trim();
            return $"{parts[0]} {parts[1]} {parts[2]} {parts[3]}";
        }

        private async Task<string?> GetFenAfterMoveFromEngineAsync(string prevFen, string uciMove)
        {
            if (_engine == null) return null;

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_fenProbeLock) { _fenProbeTcs = tcs; }

            await _engine.SendAsync($"position fen {prevFen} moves {uciMove}").ConfigureAwait(false);
            await _engine.SendAsync("d").ConfigureAwait(false);

            using var cts = new CancellationTokenSource(1500);
            try
            {
                var line = await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                // line: "Fen: <fen...>"
                var idx = line.IndexOf("Fen:", StringComparison.Ordinal);
                if (idx < 0) return null;

                var fen = line.Substring(idx + 4).Trim();
                return string.IsNullOrWhiteSpace(fen) ? null : fen;
            }
            catch
            {
                lock (_fenProbeLock) { _fenProbeTcs = null; }
                return null;
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
                _analysis.AppendText("[GUI] Log gekürzt.\r\n");
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

            // Optional: Akzent-Rand (Engine/Analyse/Danger)
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
            // nur Brett rendern, aktuelle Control-Größe
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
            meta.RepeatCount = 0; // Endlosschleife

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



        //==========================================================================================================

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

        private string _fen = "8/8/8/8/8/8/8/8 w - - 0 1";
        public string Fen
        {
            get => _fen;
            set { _fen = value ?? _fen; Invalidate(); }
        }

        //private string? _pendingFrom; // für TryBuildMove minimal
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

            // Flip: LOGICAL -> VISUAL Koordinaten (wie beim Figurenzeichnen)
            if (flipped)
            {
                fr = 7 - fr;
                fc = 7 - fc;
                tr = 7 - tr;
                tc = 7 - tc;
            }

            // Zentren der Felder
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

            // Pfeil nicht bis exakt zum Zentrum laufen lassen (wegen Figuren)
            float shortenStart = cell * 0.18f;
            float shortenEnd = cell * 0.28f;

            float sx = x1 + ux * shortenStart;
            float sy = y1 + uy * shortenStart;
            float ex = x2 - ux * shortenEnd;
            float ey = y2 - uy * shortenEnd;

            // Linienstärke zoom-stabil
            float lineW = Math.Max(2.5f, cell * 0.07f);
            float glowW = lineW * 1.7f;

            var color = Color.FromArgb(210, 0, 90, 0);        // dunkelgrün
            var glow = Color.FromArgb(60, 0, 90, 0);        // dezent

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            // Glow (optional, hilft bei hell/dunkel Kontrast)
            using (var pGlow = new Pen(glow, glowW))
            {
                pGlow.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pGlow.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(pGlow, sx, sy, ex, ey);
            }

            // Hauptlinie
            using (var p = new Pen(color, lineW))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawLine(p, sx, sy, ex, ey);
            }

            // Pfeilspitze als Dreieck (manuell, kein AdjustableArrowCap)
            float headLen = Math.Max(10f, cell * 0.28f);
            float headW = headLen * 0.65f;

            // Spitze sitzt am Ende (ex, ey), zeigt in Richtung (ux,uy)
            float hx = ex;
            float hy = ey;

            // Basis der Spitze
            float bx = hx - ux * headLen;
            float by = hy - uy * headLen;

            // Normalenvektor
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
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button != MouseButtons.Left) return;

            var (sq, ok) = PointToSquare(e.Location);
            if (!ok) return;

            // Nur ziehen, wenn da wirklich eine Figur steht
            char piece = GetPieceAtSquareFromFen(sq);
            if (piece == '\0') return;

            _isDragging = true;
            _dragFrom = sq;
            _dragPiece = piece;
            _dragPoint = e.Location;

            Capture = true;
            Invalidate();

            DragStarted?.Invoke(this, EventArgs.Empty);
        }


        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (e.Button != MouseButtons.Left) return;
            if (!_isDragging) return;

            Capture = false;

            var from = _dragFrom;

            _isDragging = false;
            _dragFrom = null;
            _dragPiece = '\0';
            Invalidate();

            DragEnded?.Invoke(this, EventArgs.Empty);

            var (to, ok) = PointToSquare(e.Location);
            if (!ok) return;

            if (string.IsNullOrWhiteSpace(from)) return;
            if (string.Equals(from, to, StringComparison.Ordinal)) return;

            UciMoveDropped?.Invoke(this, from + to);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_isDragging) return;

            _dragPoint = e.Location;
            Invalidate();
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
                int r = 8 - rank; // r=0 ist Rank 8

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

            using var light = new SolidBrush(Color.FromArgb(215, 220, 215)); // warmes off-white
            using var dark = new SolidBrush(Color.FromArgb(90, 95, 105));   // kühles dunkles grau

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

                    // Selection muss über LOGICAL Square laufen
                    var sq = ToSquare(LR(vr), LC(vc));
                    if (_isDragging && _dragFrom == sq)
                        e.Graphics.FillRectangle(sel, rect);
                }
            }

            // Pfeil nach den Feldern, vor den Figuren
            // WICHTIG: DrawBestMoveArrow muss ebenfalls Flip berücksichtigen.
            // -> siehe Hinweis unten, wie du das minimal anpasst.
            DrawBestMoveArrow(e.Graphics, origin, cell, _flipped);

            // pieces (Unicode chess)
            var placement = Fen.Split(' ')[0];
            var ranks = placement.Split('/');
            if (ranks.Length != 8) return;

            using var pieceFont = new Font("Segoe UI Symbol", Math.Max(14, cell * 0.65f), FontStyle.Regular, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // Figuren zeichnen (Startfeld während Drag überspringen)
            for (int r = 0; r < 8; r++) // r ist LOGICAL rank (FEN: 0=8. Reihe)
            {
                int file = 0;
                foreach (var ch in ranks[r])
                {
                    if (char.IsDigit(ch))
                    {
                        file += (ch - '0');
                        continue;
                    }

                    // LOGICAL square (passt zu FEN)
                    var sq = ToSquare(r, file);

                    // Wenn Drag aktiv: Figur am Startfeld nicht zeichnen (sie kommt als Overlay an der Maus)
                    if (_isDragging && _dragFrom == sq)
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

                    // Outline nur für weiße Figuren
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

            // Drag-Overlay: Figur an der Maus (ZENTRIERT)
            if (_isDragging && _dragPiece != '\0')
            {
                var uni = PieceToUnicode(_dragPiece);

                // Maus im Zentrum der Figur
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

            // Side-Indikator (rechts unten)
            DrawSideIndicator(e.Graphics, origin, boardSize);
        }

        private void DrawSideIndicator(Graphics g, Point origin, int boardSize)
        {
            const int size = 13;
            const int gap = 6;     // Abstand neben dem Brett

            // rechts neben dem Brett, unten ausgerichtet
            int x = origin.X + boardSize + gap;
            int y = origin.Y + boardSize - size;

            // Falls rechts kein Platz ist, fallback: innen rechts unten
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

            // VISUAL -> LOGICAL (bei Flip spiegeln)
            int lc = _flipped ? (7 - vc) : vc;
            int lr = _flipped ? (7 - vr) : vr;

            // Jetzt LOGICAL Koordinaten in Square umsetzen (muss zu deinem FEN-Layout passen)
            return (ToSquare(lr, lc), true);
        }


        private static string ToSquare(int r, int c)
        {
            // r=0 ist Rank 8
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
            sb.Append(' ').Append(_side);
            sb.Append(' ').Append(string.IsNullOrWhiteSpace(_castling) ? "-" : _castling);
            sb.Append(' ').Append(string.IsNullOrWhiteSpace(_ep) ? "-" : _ep);
            sb.Append(' ').Append(_halfmove.ToString(CultureInfo.InvariantCulture));
            sb.Append(' ').Append(_fullmove.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
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

            // Capture merken (für halfmove)
            bool isCapture = _b[tr, tf] != '\0';

            _b[fr, ff] = '\0';

            // En-passant minimal
            if ((piece == 'P' || piece == 'p') && _ep != "-" && _ep == to && _b[tr, tf] == '\0')
            {
                if (piece == 'P') _b[tr + 1, tf] = '\0';
                else _b[tr - 1, tf] = '\0';
                isCapture = true;
            }

            // Rochade minimal
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

            // EP setzen (nur Doppelzug)
            _ep = "-";
            if (piece == 'P' && fr == 6 && tr == 4) _ep = $"{(char)('a' + ff)}3";
            if (piece == 'p' && fr == 1 && tr == 3) _ep = $"{(char)('a' + ff)}6";

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
            // 1) Tag-Pairs entfernen
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

            // 2) ; Kommentare bis EOL entfernen
            s = Regex.Replace(s, @";[^\r\n]*", " ");

            // 3) { } Kommentare entfernen
            s = Regex.Replace(s, @"\{[^}]*\}", " ");

            // 4) ( ) Varianten verschachtelt entfernen
            s = RemoveParenthesesBlocks(s);

            // 5) NAGs $123 entfernen
            s = Regex.Replace(s, @"\$\d+", " ");

            // 6) Symbolische NAGs entfernen
            s = Regex.Replace(s, @"(\!\?|\?\!|\!\!|\?\?|\!|\?)", " ");

            // 7) Ergebnis-Tokens entfernen
            s = Regex.Replace(s, @"\b(1-0|0-1|1/2-1/2|\*)\b", " ");

            // 8) Move-Nummern entfernen (z.B. "1." oder "34..." )
            s = Regex.Replace(s, @"\b\d+\.(\.\.)?\b", " ");

            // 9) Tokenisieren
            var tokens = s.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var moves = new List<string>();
            foreach (var t in tokens)
            {
                // SAN grob erkennen: Rochade oder enthält Buchstaben/Ziffern
                // Die Ausführung/Validierung macht Gera.Chess beim Import.
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
        public static readonly Color AccentAnalyse = Color.FromArgb(70, 160, 90);   // Grün
        public static readonly Color Danger = Color.FromArgb(200, 70, 70);          // Rot

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
            Text = "FEN eingeben";
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
                Text = "FEN: placement side castling ep [halfmove fullmove]\n(4 Felder sind ok – werden zu '... 0 1' ergänzt)",
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
            var cancel = new System.Windows.Forms.Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, Width = 130, Height = 34, Margin = new Padding(8, 8, 0, 8) };
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

}
